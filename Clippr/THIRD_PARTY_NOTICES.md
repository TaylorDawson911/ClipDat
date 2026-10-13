# Third party notices

## FFmpeg

Clippr dynamically links to the FFmpeg `avformat`, `avcodec`, and `avutil` libraries for remuxing captured H.264 and AAC packets into MP4 and Matroska files. The dependency is pinned by `vcpkg.json` and `vcpkg-configuration.json`. The reviewed FFmpeg 9.0.2 configuration builds shared libraries, disables the command-line tools, and does not enable `--enable-gpl` or `--enable-nonfree`; external GPL encoders such as libx264 and libx265 are disabled. See `FFmpegBuildConfiguration.txt` for the reviewed build summary.

FFmpeg is generally licensed under LGPL 2.1 or later, though individual components and optional external libraries can carry different terms. The app output includes FFmpeg's copyright/license text and this notice. Before distributing a release, provide the exact corresponding FFmpeg source (including the vcpkg patches and build configuration), preserve LGPL replaceability/relinking rights, and have the final package reviewed for compliance. This notice is a technical dependency record, not legal advice.

FFmpeg licensing details: https://ffmpeg.org/legal.html
