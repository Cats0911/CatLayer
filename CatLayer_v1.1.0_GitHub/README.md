CatLayer v1.1.0

CatLayer는 Windows 화면 위에 이미지/GIF, 텍스트, 타이머, OBS Program 화면, 웹페이지와 로컬 웹젯을 띄우고 자유롭게 배치할 수 있는 범용 오버레이 도구입니다.

Copyright © 2026 과출. All rights reserved.
저작권/재배포 조건은 TERMS_KO.txt를 확인해 주세요.

[설치]
1. ZIP 전체를 일반 폴더에 압축 해제합니다.
2. INSTALL.bat을 실행합니다.
3. 설치 위치: %LOCALAPPDATA%\CatLayer\App
4. 설치 후 Windows 설정 > 설치된 앱 / 제어판 > 프로그램 제거에 CatLayer가 표시됩니다.
5. 바탕화면 및 시작 메뉴에 CatLayer 바로가기가 생성됩니다.

첫 빌드 시 Microsoft WebView2 SDK 1.0.4129.50 파일이 없으면 INSTALL.bat이 Microsoft NuGet에서 한 번 내려받습니다.
웹 오버레이 실행에는 Microsoft Edge WebView2 Runtime이 필요합니다.
Visual Studio 프로젝트는 필요하지 않습니다.

[휴대/직접 실행]
- RUN.bat: 현재 소스를 Windows 기본 .NET Framework 4.x csc.exe로 빌드한 뒤 실행합니다.
- INSTALL.bat: 빌드 후 CatLayer를 사용자 계정에 설치합니다.

[주요 기능]
- 이미지 / GIF / WebP / 텍스트 / 타이머 / OBS Program / 웹 오버레이
- PNG/JPG/JPEG/BMP/GIF/WebP 드래그앤드롭
- 화면 영역 캡처(F1)
- 항상 위 표시, 이동/크기/투명도/레이어 순서 조절
- 개별 표시/숨김, 위치/크기 잠금, 사용자 지정 이름
- 다중 선택, 그룹화/해제, 그룹 이동/확대·축소
- 자유 회전, 좌우/상하 반전
- 배치 자석 / 회전 자석
- 이미지 Shift+크기 조절 자르기
- 프리셋 저장/불러오기/삭제/가져오기 및 프리셋 단축키
- .catlayergroup 그룹 공유 파일
- .catlayerweb 로컬 웹젯 패키지
- 실행 취소, 전체 삭제, 전체 숨김, 설정 복구
- 단일 실행 및 트레이 백그라운드
- 멀티모니터/화면 밖 오버레이 자동 복구
- GitHub Releases 기반 사용자 승인 자동 업데이트

[기본 단축키]
- F1: 화면 영역 캡처
- F8: 고정 ↔ 편집 모드
- F9: 전체 표시 / 숨김
- F10: 전체 웹 조작 모드 진입 / 종료
- ESC: 웹 조작 모드 종료
- Ctrl+C: 선택 오버레이 복제(OBS 제외)
- Ctrl+V: 클립보드 정적 이미지 붙여넣기
- Ctrl+G: 그룹화/그룹해제
- Ctrl+Shift+G: 그룹 해제
- Q / E: 회전 -1° / +1°
- Shift+Q / Shift+E: 회전 -10° / +10°
- H / V: 좌우 / 상하 반전
- R: 회전 각도 0°(반전 유지)
- Shift+R: 회전/반전 전체 초기화
- 방향키: 1px 이동
- Shift+방향키: 10px 이동
- Delete: 선택 오버레이 삭제

F1/F8/F9 및 편집 단축키는 메뉴 > 설정 > 사용자 지정 단축키에서 변경할 수 있습니다.
Ctrl+C / Ctrl+V는 CatLayer 예약키입니다.

[웹 오버레이]
- 웹 버튼에서 HTTP/HTTPS 주소, .html/.htm, .catlayerweb 파일을 추가할 수 있습니다.
- 편집 모드에서 웹 오버레이를 더블클릭하면 해당 웹만 직접 조작할 수 있습니다.
- 웹 바깥을 클릭하거나 ESC를 누르면 일반 편집 상태로 돌아갑니다.
- F10은 전체 웹 조작 모드의 백업/전역 진입 방식으로 유지됩니다.
- 주소 변경, 새로고침, 뒤로/앞으로, 페이지 줌, 웹 투명도, 커스텀 CSS를 지원합니다.
- 쿠키/캐시/로그인 정보는 %LOCALAPPDATA%\CatLayer\WebData 에 저장되고 프리셋/그룹/웹젯 파일에는 포함되지 않습니다.
- 다운로드, 카메라/마이크/위치 등 민감 권한은 기본 차단합니다.

[로컬 HTML / CatLayerWeb]
- 로컬 HTML은 원본 위치를 직접 file://로 열지 않고 CatLayer 관리 폴더로 안전한 리소스만 복사해 표시합니다.
- 실행 파일 계열(EXE/BAT/CMD/PS1/MSI/DLL)은 로컬 웹 패키지에 포함하지 않습니다.
- .catlayerweb 파일은 CatLayer로 드래그하거나 설치판에서 더블클릭해 추가할 수 있습니다.
- 신뢰된 로컬 웹젯에는 제한된 크기 변경 API가 제공되어 접기/펼치기 시 현재 웹 오버레이 크기를 조절할 수 있습니다.
- 범용 네이티브 HostObject는 노출하지 않습니다.

[기본 웹젯 5종]
examples 폴더:
- 메모장.catlayerweb
- 그림판.catlayerweb
- 미니계산기.catlayerweb
- 체크리스트.catlayerweb
- 타이머보드.catlayerweb

기본 웹젯은 인터넷 없이 동작하며 상태를 WebData/localStorage에 자동 저장합니다.
접기/펼치기를 지원하며 CatLayer의 로컬 웹젯 크기 변경 기능을 사용합니다.

[OBS Bridge]
1. OBS 실행
2. OBS > 도구 > 스크립트
3. + 버튼
4. obs\CatLayer_OBS_Bridge.lua 추가
5. CatLayer에서 OBS 화면 추가

CatLayer는 OBS Program Windowed Projector + DWM Thumbnail 방식을 사용합니다.

[프리셋 / 그룹 파일]
- 기존 프리셋 V1~V6 불러오기 호환을 유지합니다.
- 웹 오버레이가 포함된 프리셋은 V7 정보를 사용합니다.
- .catlayergroup은 선택한 그룹/다중 선택 항목의 필요한 리소스를 묶어 다른 사용자에게 공유할 수 있습니다.
- OBS 중복 항목은 그룹 불러오기 시 안전하게 처리합니다.

[업데이트]
- CatLayer는 Cats0911/CatLayer GitHub Releases의 최신 정식 Release를 확인합니다.
- Draft / Pre-release는 자동 업데이트 대상에서 제외합니다.
- 새 버전이 있으면 사용자에게 먼저 묻고 승인한 경우에만 업데이트합니다.
- 업데이트 ZIP은 SHA-256 검증 후 적용하며 실패 시 교체하지 않습니다.
- 시작 시 업데이트 안내에서 나중에 하기 / 다시 안 보기 옵션을 사용할 수 있고, 수동 업데이트 확인은 계속 가능합니다.

[설정 및 사용자 데이터]
%LOCALAPPDATA%\CatLayer\
  config.txt
  config.txt.bak
  Assets\
  Sounds\
  Presets\
  Undo\
  WebData\
  WebFiles\
  crash.log

config.txt 로드 실패 시 config.txt.bak 복구를 시도합니다.

[제거]
- Windows 설정 > 설치된 앱 / 제어판 > 프로그램 제거
- 또는 %LOCALAPPDATA%\CatLayer\App\Uninstall.exe
- 프로그램만 제거: 설정/프리셋/사용자 데이터 유지
- 완전 삭제: 프로그램과 사용자 데이터까지 제거

[기존 LightOverlay 사용자]
기존 %LOCALAPPDATA%\LightOverlay 데이터가 있으면 필요한 사용자 데이터를 CatLayer로 이전합니다.
기존 LightOverlay 원본 데이터는 자동 삭제하지 않습니다.

[v1.1.0 핵심 변경]
- WebView2 웹 오버레이 정식 확장
- 더블클릭 웹 직접 조작 + F10 전체 웹 조작
- ESC 웹 조작 종료
- 웹 투명도 / CSS / 페이지 줌
- 로컬 HTML / .catlayerweb 지원 및 로컬 웹 보안 강화
- 제한된 로컬 웹젯 resize API
- 기본 웹젯 5종 추가
- .catlayergroup 공유 파일
- WebP 이미지 지원
- GitHub 자동 업데이트
- 전체적인 CPU/RAM 및 반복 작업 최적화
- 웹 오버레이 리사이즈 후 투명 배경 재합성 보정

[버전 관리]
VERSION.txt가 프로그램/설치/업데이트 버전 정보의 기준입니다.
현재 버전: 1.1.0
