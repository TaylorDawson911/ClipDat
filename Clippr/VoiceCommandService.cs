using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace Clippr;

/// <summary>
/// On-device, constrained voice commands. This service never uses a topic/dictation grammar
/// and does not persist recognized speech or audio.
/// </summary>
internal sealed class VoiceCommandService : IAsyncDisposable
{
    private readonly SpeechRecognizer _recognizer;
    private readonly (string Phrase, string Action)[] _commands;
    private readonly string _wakePhrase;
    private readonly string _minimumConfidence;
    private readonly TimeSpan _cooldown;
    private readonly Action<string> _commandHandler;
    private readonly Action<string> _statusHandler;
    private readonly Action<string, string> _transcriptHandler;
    private long _lastCommandTicks;
    private int _disposed;

    private VoiceCommandService(SpeechRecognizer recognizer, (string Phrase, string Action)[] commands,
        string wakePhrase, string minimumConfidence, TimeSpan cooldown, Action<string> commandHandler,
        Action<string> statusHandler, Action<string, string> transcriptHandler)
    {
        _recognizer = recognizer;
        _commands = commands;
        _wakePhrase = wakePhrase;
        _minimumConfidence = minimumConfidence;
        _cooldown = cooldown;
        _commandHandler = commandHandler;
        _statusHandler = statusHandler;
        _transcriptHandler = transcriptHandler;
    }

    public static async Task<VoiceCommandService> StartAsync(string wakePhrase, string minimumConfidence,
        int cooldownSeconds, IReadOnlyDictionary<string, string> commandPhrases,
        Action<string> commandHandler, Action<string> statusHandler, Action<string, string> transcriptHandler)
    {
        SpeechRecognizer? recognizer = null;
        VoiceCommandService? service = null;
        try
        {
            var language = SpeechRecognizer.SystemSpeechLanguage;
            if (language is null || !SpeechRecognizer.SupportedGrammarLanguages.Any(
                    supported => string.Equals(supported.LanguageTag, language.LanguageTag, StringComparison.OrdinalIgnoreCase)))
                throw new VoiceRecognitionException(
                    "Offline speech recognition is unavailable for the Windows speech language. Install its Speech language feature in Windows Settings, then try again.");

            recognizer = new SpeechRecognizer(language);
            var normalizedWakePhrase = Normalize(wakePhrase);
            var commands = VoiceCommandCatalog.Commands.Select(command =>
                (Phrase: Normalize(commandPhrases[command.Key]), command.Action)).ToArray();
            var phrases = commands.Select(command => normalizedWakePhrase.Length == 0
                    ? command.Phrase
                    : $"{normalizedWakePhrase} {command.Phrase}")
                .ToArray();
            recognizer.Constraints.Add(new SpeechRecognitionListConstraint(phrases, "clipdat-commands"));
            var compilation = await recognizer.CompileConstraintsAsync();
            if (compilation.Status != SpeechRecognitionResultStatus.Success)
                throw new VoiceRecognitionException(
                    $"Offline speech grammar could not compile ({compilation.Status}). Check the installed Windows speech language feature.");

            service = new VoiceCommandService(recognizer, commands, normalizedWakePhrase, minimumConfidence,
                TimeSpan.FromSeconds(Math.Clamp(cooldownSeconds, 1, 10)), commandHandler, statusHandler,
                transcriptHandler);
            recognizer = null;
            service._recognizer.HypothesisGenerated += service.OnHypothesisGenerated;
            service._recognizer.ContinuousRecognitionSession.ResultGenerated += service.OnResultGenerated;
            service._recognizer.ContinuousRecognitionSession.Completed += service.OnSessionCompleted;
            await service._recognizer.ContinuousRecognitionSession.StartAsync();
            statusHandler("Listening with Windows offline speech recognition on the default microphone.");
            transcriptHandler("", "Listening for a command…");
            return service;
        }
        catch (UnauthorizedAccessException ex)
        {
            if (service is not null) await service.DisposeAsync();
            else recognizer?.Dispose();
            throw new VoiceRecognitionException(
                "Microphone access is blocked. Allow ClipDat under Windows Settings > Privacy & security > Microphone.", ex);
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80070005))
        {
            if (service is not null) await service.DisposeAsync();
            else recognizer?.Dispose();
            throw new VoiceRecognitionException(
                "Microphone access is blocked. Allow ClipDat under Windows Settings > Privacy & security > Microphone.", ex);
        }
        catch (VoiceRecognitionException)
        {
            if (service is not null) await service.DisposeAsync();
            else recognizer?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            if (service is not null) await service.DisposeAsync();
            else recognizer?.Dispose();
            throw new VoiceRecognitionException($"Windows voice recognition could not start: {ex.Message}", ex);
        }
    }

    private void OnHypothesisGenerated(SpeechRecognizer sender, SpeechRecognitionHypothesisGeneratedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        var words = args.Hypothesis.Text?.Trim();
        if (!string.IsNullOrWhiteSpace(words)) _transcriptHandler(words, "Hearing…");
    }

    private void OnResultGenerated(SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) != 0 || args.Result.Status != SpeechRecognitionResultStatus.Success)
            return;
        var heard = args.Result.Text?.Trim() ?? "";
        if (heard.Length == 0) return;
        if (_minimumConfidence == "High" && args.Result.Confidence != SpeechRecognitionConfidence.High)
        {
            _transcriptHandler(heard, "Heard · below High confidence");
            return;
        }
        if (_minimumConfidence == "Medium" && args.Result.Confidence is SpeechRecognitionConfidence.Low or SpeechRecognitionConfidence.Rejected)
        {
            _transcriptHandler(heard, "Heard · below Medium confidence");
            return;
        }

        var phrase = Normalize(heard);
        if (_wakePhrase.Length != 0)
        {
            var prefix = _wakePhrase + " ";
            if (!phrase.StartsWith(prefix, StringComparison.Ordinal))
            {
                _transcriptHandler(heard, "Heard · wake phrase missing");
                return;
            }
            phrase = phrase[prefix.Length..];
        }
        var action = _commands.Where(command => phrase == command.Phrase)
            .Select(command => command.Action).FirstOrDefault();
        if (action is null)
        {
            _transcriptHandler(heard, "Heard · no matching command");
            return;
        }

        var now = DateTime.UtcNow.Ticks;
        while (true)
        {
            var previous = Interlocked.Read(ref _lastCommandTicks);
            if (previous != 0 && now - previous < _cooldown.Ticks)
            {
                _transcriptHandler(heard, "Ignored · command cooldown");
                return;
            }
            if (Interlocked.CompareExchange(ref _lastCommandTicks, now, previous) == previous) break;
        }
        _transcriptHandler(heard, "Command recognised");
        try { _commandHandler(action); }
        catch (Exception) { }
    }

    private void OnSessionCompleted(SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionCompletedEventArgs args)
    {
        if (Volatile.Read(ref _disposed) == 0 && args.Status != SpeechRecognitionResultStatus.Success)
            _statusHandler($"Voice recognition stopped ({args.Status}). Re-enable it in Settings to try again.");
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _recognizer.HypothesisGenerated -= OnHypothesisGenerated;
        _recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
        _recognizer.ContinuousRecognitionSession.Completed -= OnSessionCompleted;
        try { await _recognizer.ContinuousRecognitionSession.CancelAsync(); }
        catch (Exception) { }
        _recognizer.Dispose();
    }

    private static string Normalize(string value) => string.Join(' ',
        (value ?? "").Trim().ToLowerInvariant().Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
}

internal sealed class VoiceRecognitionException : Exception
{
    public VoiceRecognitionException(string message) : base(message) { }
    public VoiceRecognitionException(string message, Exception innerException) : base(message, innerException) { }
}
