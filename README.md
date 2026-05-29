# Mashiro Launcher

가볍고 깔끔한 윈도우용 마인크래프트 런처입니다. 자바 에디션과 베드락 에디션을 한 앱에서 다룰 수 있고, Fabric과 NeoForge 모드로더, Modrinth 모드 브라우저까지 기본 내장시켜 놨습니다.

## 주요 기능

- **자바 에디션**: 바닐라 / Fabric / NeoForge 실행
- **베드락 에디션**: Microsoft Store 없이 자체 설치 및 실행 (이미 스토어에서 다운해놨다면 그걸 실행합니다)
- **계정**: Microsoft 로그인(WebView2 임베디드) 또는 오프라인 모드, 토큰은 Windows DPAPI로 암호화 저장
- **인스턴스 관리**: 생성·복제·삭제, JVM 개별 설정, .zip 백업/복원 등
- **Modrinth 연동**: 모드 검색·설치·토글, 외부 .jar 추가, .mrpack 임포트 등
- **로그 뷰어**: 런처 및 인스턴스 실시간 로그 확인
- **바닐라 설정 자동 가져오기**: 새 인스턴스 생성 시 기존 options.txt, servers.dat 자동 복사

## 다운로드

[최신 빌드 받기](https://github.com/AkaiMashiro/MashiroLauncher/releases/latest)

## 직접 빌드

.NET 10 SDK가 필요합니다 (`global.json` 참고).

```bash
dotnet build MashiroLauncher.slnx
dotnet run --project src/MashiroLauncher.App/MashiroLauncher.App.csproj
```

테스트:

```bash
dotnet test tests/MashiroLauncher.Core.Tests/MashiroLauncher.Core.Tests.csproj
```

## 인증과 보안

Microsoft 로그인은 Mojang 승인을 받은 Mashiro Launcher 자체 Azure 앱(Microsoft identity platform v2.0 + PKCE)을 사용하고, 비밀번호는 Microsoft의 공식 로그인 페이지에 입력하기 때문에 런처가 직접 보지 못합니다.

로그인 결과로 받은 토큰(refresh token + Minecraft access token)만 계정별로 `data/accounts/{uuid}.json`에 저장되며, Windows에서는 **DPAPI (CurrentUser)** 로 암호화됩니다. 다른 사용자 계정이나 다른 PC에서는 복호화할 수 없습니다. 언제든 계정 로그아웃으로 파일을 삭제할 수 있습니다.

## 라이선스

[MIT](LICENSE)
