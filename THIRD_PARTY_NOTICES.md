# 영상 구성요소

앱에 포함하는 네이티브 영상 구성요소:

| 구성요소 | 고정 패키지 버전 | 패키지의 라이선스 표기 | 원본 |
|---|---|---|---|
| LibVLCSharp / LibVLCSharp.WPF | 3.10.1 | LGPL-2.1-or-later | https://code.videolan.org/videolan/LibVLCSharp |
| VideoLAN.LibVLC.Windows | 3.0.23.1 (엔진 3.0.23) | LGPL-2.1-or-later | https://code.videolan.org/videolan/libvlc-nuget |

Copyright © VideoLAN and contributors. 동적으로 연결되는 x64 네이티브 라이브러리와 플러그인을 앱의 libvlc/win-x64 폴더에 둔다.
단일 파일 내부에 결합하지 않으며, 사용자가 호환 라이브러리를 교체할 수 있는 배포 구조를 유지한다.
LGPL 라이선스 본문은 함께 배포하는 licenses/LGPL-2.1.txt에 있다.
대응 소스 및 해당 빌드의 구성은 위 VideoLAN 패키지 저장소와 https://code.videolan.org/videolan/vlc 의 3.0.23 릴리스에서 확인한다.
최종 설치본을 발행할 때는 배포 바이너리에 대응하는 소스 제공 방식과 구성요소별 고지를 함께 제공한다.

별도 영상 서버는 MediaMTX 1.21.0 기준으로 로컬 검증했다.
설치형 EXE에는 MediaMTX 1.21.0(MIT, https://github.com/bluenviron/mediamtx/releases/tag/v1.21.0)을 MediaMTX 폴더에 포함하고 해당 배포의 LICENSE를 함께 제공한다. 원본 소스도 같은 릴리스에서 받을 수 있다. 설치 프로그램은 영상 서버를 실행하거나 운영 설정·비밀번호를 배포하지 않는다. 사용자가 설치 PC의 기존 설정을 연결한 뒤 앱에서 서버 시작에 동의할 때 실행한다.

로컬 테스트에만 FFmpeg 9.0.1 Gyan essentials 빌드(GPLv3, https://www.gyan.dev/ffmpeg/builds/)를 사용한다.
테스트용 FFmpeg는 artifacts/media-tools에만 두고 제품 배포물에 포함하지 않는다.
