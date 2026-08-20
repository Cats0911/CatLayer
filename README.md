# CatLayer

CatLayer는 Windows 화면 위에 이미지, GIF, 텍스트, 타이머, OBS Program 화면, 웹페이지/로컬 HTML 등을 표시할 수 있는 범용 오버레이 도구입니다.

현재 저장소에는 **CatLayer 1.1.0 개발 소스**가 올라갑니다.  
정식 사용자용 배포 파일은 GitHub의 **Releases**에서 제공할 예정입니다.

## 주요 기능

- 이미지 / GIF 오버레이
- 텍스트 오버레이
- 타이머 / 스톱워치
- OBS Program 화면 오버레이
- 웹사이트 / 로컬 HTML / CatLayerWeb 오버레이
- 투명도, 회전, 반전, 크기 조절
- 클릭 통과 및 편집 모드
- 그룹 / 프리셋
- 화면 영역 캡처
- 다중 오버레이 정렬 및 스냅

## 실행 및 설치

CatLayer는 Visual Studio 프로젝트 없이 Windows의 .NET Framework 컴파일러를 사용하는 구조입니다.

일반 사용자는 정식 배포 ZIP을 받은 뒤 `INSTALL.bat`을 실행하는 방식을 권장합니다.

개발 소스 실행 및 빌드 관련 파일:

- `src/CatLayer.cs`
- `src/Uninstall.cs`
- `RUN.bat`
- `INSTALL.bat`
- `tools/Prepare-WebView2.ps1`

웹 오버레이에는 Microsoft Edge WebView2 Runtime이 필요합니다.

## OBS

OBS 화면 오버레이는 OBS의 Program Windowed Projector와 DWM Thumbnail 방식을 사용합니다.

OBS에서 다음 스크립트를 추가해 사용할 수 있습니다.

`obs/CatLayer_OBS_Bridge.lua`

## 예제

`examples` 폴더에는 로컬 HTML 및 CatLayerWeb 테스트용 예제가 포함되어 있습니다.

## 소스 공개에 대하여

이 저장소는 CatLayer의 동작 확인, 투명성, 코드 검토 및 개인적인 학습을 위해 소스를 공개합니다.

**오픈 소스 라이선스를 부여하는 저장소가 아닙니다.**  
원본 또는 수정본 CatLayer의 무단 재배포는 허용되지 않습니다.

자세한 조건은 [TERMS.md](TERMS.md)를 확인해 주세요.

## 공식 배포

공식 배포본은 이 GitHub 저장소의 Releases를 통해 제공하는 것을 기준으로 합니다.

비공식적으로 재업로드된 실행 파일이나 수정본은 CatLayer 공식 배포본으로 간주하지 않습니다.
