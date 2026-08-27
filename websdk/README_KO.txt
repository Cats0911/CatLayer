CatLayer Web Widget SDK 준비 파일

- catlayer-widget.css: 향후 기본 웹 위젯들의 공통 디자인 토큰/기본 스타일용
- catlayer-widget.js: window.catlayer.resize(width, height)를 안전하게 호출하는 최소 헬퍼

보안 원칙
- 외부 EXE 실행 금지
- 임의 로컬 파일 접근 금지
- 다운로드 기능 사용 금지
- 일반 native bridge 없음
- 크기 변경은 window.catlayer.resize(width, height)만 사용
