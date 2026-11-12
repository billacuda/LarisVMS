# 설치 및 업그레이드

웹 서버, 녹화 노드, 미디어 프록시를 설치하고 최신 상태로 유지하는 방법을 설명합니다.

각 구성 요소에는 전용 설치 프로그램(`LarisVMS-Web`, `LarisVMS-Node`, `LarisVMS-Proxy`, 모두 `.msi`)이 있습니다. 더블클릭하면 설정을 입력하라는 메시지가 표시되며, 명령줄에서 `msiexec /i <file>.msi NAME=value … /qn`으로 전달할 수도 있습니다. 지정하지 않은 항목은 입력을 요청하며, 업그레이드는 이전 설치의 설정을 재사용합니다.

## 웹 서버

1. 서버에서 접근할 수 있는 SQL Server(통합 보안 또는 SQL 인증) 또는 MariaDB 서버를 준비합니다([MariaDB](#mariadb) 참조).
2. `LarisVMS-Web.msi`를 실행합니다. 설정:
   - `HTTPSPORT`: HTTPS 포트(기본값 8444). 설치 프로그램이 TCP와 UDP([HTTP/3](/Help/security#http3)) 모두에 대해 방화벽을 엽니다.
   - `CERTPATH`, `CERTPASSWORD`: 서버 인증서(`.pfx`). 인증서가 준비될 때까지는 비워 두어 자체 서명 인증서를 사용합니다. 설치 프로그램은 계속하기 전에 비밀번호로 파일이 열리는지 확인합니다. 파일 공유의 경우 서비스 계정도 읽을 수 있어야 합니다.
   - `SERVICEACCOUNT`, `SERVICEPASSWORD`: 서비스를 특정 계정(예: SQL 통합 보안 권한이 있는 계정)으로 실행합니다. 비워 두면 LocalSystem으로 실행됩니다. MariaDB에서는 필요하지 않습니다.
3. `https://<host>:8444/`로 이동합니다. 설정 마법사가 데이터베이스, 첫 관리자 계정, 저장소, 노드 등록을 차례로 안내합니다. 서버 자체의 브라우저나 로컬 네트워크의 브라우저는 바로 진행됩니다. 그 외의 곳에서는 먼저 서버의 `%ProgramData%\LarisVMS\setup-token.txt`(관리자만 읽을 수 있음)에 있는 설정 토큰을 요구하므로, 인터넷의 누구도 먼저 설정할 수 없습니다. 로컬 네트워크를 포함한 다른 모든 컴퓨터에서 토큰을 요구하려면, 마법사를 열기 전에 `appsettings.Production.json`에 `"Setup": { "AlwaysRequireToken": true }`를 설정하십시오. 이 파일은 관리자 계정이 만들어지면 삭제됩니다.

SQL 로그인의 비밀번호나 MariaDB 비밀번호는 서버에 암호화되어 저장되므로(DPAPI, 이 컴퓨터에서만 유효) `setup-generated.json`을 다른 컴퓨터로 복사해도 비밀번호가 작동하지 않습니다. 대신 그곳에서 마법사를 다시 실행하십시오.

마법사에서 기존 LarisVMS 데이터베이스(예: 서버를 다시 설치한 후)를 지정하면 그 데이터베이스를 그대로 사용합니다. 필요하면 업그레이드되고, 아무것도 새로 채우거나 덮어쓰지 않으며, 나머지 단계는 건너뜁니다. LarisVMS 데이터베이스가 아니면 거부됩니다. 설정이 완료되면 마법사를 다시 열 수 없습니다.

서버에는 .NET 런타임이 필요하지 않으며 포함되어 있습니다. 인증서는 나중에 설치 폴더의 `appsettings.Production.json`에서도 변경할 수 있습니다. 같은 경로에 갱신된 `.pfx`를 두면 다시 시작하지 않아도 1분 안에 반영됩니다.

## MariaDB

LarisVMS는 SQL Server 대신 MariaDB 10.11 이상을 사용할 수 있습니다. 설정 마법사에서 **MariaDB**를 선택하고 호스트, 포트(기본값 3306), 데이터베이스 이름, MariaDB 사용자와 비밀번호를 입력합니다. LarisVMS가 사용할 수 있는 MariaDB용 Windows 로그인은 없으므로 항상 사용자와 비밀번호가 필요합니다.

- **사용자**에게는 데이터베이스를 만들 권한이 있어야 합니다. 또는 먼저 `utf8mb4` 문자 집합으로 직접 만들고 해당 사용자에게 `ALL` 권한을 부여하십시오:

  ```sql
  CREATE DATABASE larisvms CHARACTER SET utf8mb4 COLLATE utf8mb4_uca1400_as_ci;
  CREATE USER 'larisvms'@'%' IDENTIFIED BY 'a-strong-password';
  GRANT ALL ON larisvms.* TO 'larisvms'@'%';
  ```

- **암호화:** 연결은 TLS를 사용합니다. MariaDB가 이 컴퓨터에 있거나 **이 서버의 인증서 신뢰**를 선택한 경우(다른 컴퓨터의 자체 서명 인증서용)를 제외하고 서버의 인증서를 확인합니다.
- **11.4 이전의 MariaDB는 TLS가 꺼져 있으며** 인증서를 설정할 때까지 그대로입니다. LarisVMS는 TLS 없이는 다른 컴퓨터로 비밀번호를 보내지 않습니다. `my.ini`의 `[mysqld]` 섹션에 인증서와 키를 추가하고 MariaDB를 다시 시작하십시오. 자체 서명이라면 **이 서버의 인증서 신뢰**도 선택하십시오:

  ```ini
  [mysqld]
  ssl_cert = C:/MariaDB/certs/server-cert.pem
  ssl_key = C:/MariaDB/certs/server-key.pem
  ```

  MariaDB 11.4 이상은 인증서를 스스로 설정합니다.
- **백업**에는 LarisVMS 서버에 MariaDB 클라이언트 도구가 필요합니다([백업](/Help/backups) 참조).
- 기존 SQL Server 설치를 MariaDB로 옮길 수는 없습니다. MariaDB는 새 설치용입니다.

## 노드

각 녹화 컴퓨터에서 `LarisVMS-Node.msi`를 실행합니다. 다음이 필요합니다:

- `SERVERURL`과 `REGISTRATIONKEY`: 서버의 주소와 **노드 → 업데이트 및 등록**의 **등록 키**입니다. 노드가 처음 등록할 때만 사용됩니다. 키를 변경해도 기존 노드에는 영향이 없습니다.
- **FFmpeg**: 포함되어 있지 않습니다. 먼저 `winget install ffmpeg --scope machine`으로 설치하십시오. 설치 프로그램이 찾으며, 찾지 못하면 `FFMPEGPATH`를 설정하십시오.
- `STORAGEROOT`: 노드가 녹화하는 위치입니다. 나중에 노드의 편집 페이지에서 설정할 수도 있습니다.

선택 사항: `ARCHIVEROOT`, `LIVEPORT`(기본값 8554), `LIVEALLOWFROM`(방화벽에서 라이브 포트로의 접속을 허용할 주소. LarisVMS 서버와 프록시를 IP 또는 CIDR 블록으로 쉼표로 구분하여 지정하며, 비워 두면 모두 허용), `INSECURETLS=1`(더 이상 필요하지 않음: 노드는 처음 사용 시 서버의 자체 서명 인증서를 신뢰합니다. [서버 인증서](/Help/nodes#server-certificate) 참조), 직접 스트리밍 엔드포인트(`CLIENTPORT`, `CLIENTENDPOINTHOST`, `CLIENTPFXPATH`, `CLIENTPFXPASSWORD`), SMB 저장소에 접근할 수 있는 계정을 위한 `SERVICEACCOUNT` / `SERVICEPASSWORD`.

AI 감지에는 NVIDIA 컴퓨터에 CUDA Toolkit 12.x와 cuDNN 9.x도 필요합니다. 설치 프로그램은 이를 찾으면 DLL을 제자리에 복사합니다. [AI 감지](/Help/ai-detection#hardware)를 참조하십시오.

설치 후 노드는 **노드**에 **승인 대기 중**으로 나타납니다. **승인**을 클릭할 때까지 아무것도 녹화하지 않으며 카메라도 할당되지 않으므로, 직접 설치한 노드만 승인하십시오. 그런 다음 각 카메라의 편집 페이지에서 노드에 카메라를 할당합니다.

## 미디어 프록시

`SERVERURL`, `REGISTRATIONKEY`, `CLIENTPORT`(기본값 4443)를 지정하여 `LarisVMS-Proxy.msi`를 실행합니다. 인증서 선택 설정은 노드와 같습니다. 새 프록시는 꺼진 상태로 시작합니다. **설정 → 미디어 프록시**에서 **사용**을 켠 다음 각 노드의 페이지에서 선택하십시오([노드](/Help/nodes#proxies) 참조).

## 업데이트

**업그레이드:** 더 새로운 `.msi`를 실행합니다. 기존 설치를 찾아 바로 **업그레이드 준비 완료**로 이동합니다. 설정이 기억되므로 도메인 서비스 계정의 비밀번호 외에는 다시 입력할 것이 없습니다. 웹 서버는 시작할 때 데이터베이스 변경 사항을 스스로 적용합니다. 업그레이드는 녹화, 구성 파일, 노드 등록, 데이터 보호 키를 건드리지 않으며, 설치에 실패하면 롤백됩니다.

**노드와 프록시는 스스로 업데이트됩니다.** 새 웹 설치는 포함된 노드 및 프록시 빌드를 **노드**의 **빌드** 섹션에 **보류 중**으로 등록합니다. 승인을 기다리는 것은 최신 노드 빌드와 최신 프록시 빌드뿐이며, 이전 빌드는 **대체됨**이 됩니다. **빌드 기록**에는 모든 빌드가 나열됩니다. 하나를 승인하면 이전 버전의 모든 노드가 다음 체크인 때 이를 다운로드하고, 확인하고, 설치합니다. 서버는 모든 업데이트에 서명 키로 서명합니다(**설정 → 암호화 및 서명** 참조). 노드와 프록시는 등록할 때 그 키를 보관하며, 서버와의 연결이 변조되더라도 그 키로 서명되지 않은 것은 설치하지 않습니다. 끄려면 **노드 → 업데이트 및 등록**의 **자동 업데이트**를 사용하십시오.

**새 버전:** 하루에 한 번(그리고 서비스가 시작될 때) 서버가 LarisVMS의 GitHub 릴리스를 확인합니다. 새 버전이 나오면 설정을 변경할 수 있는 사용자에게 상단 표시줄에 릴리스 노트로 연결되는 **✨ 버전 X을(를) 사용할 수 있습니다**가 표시됩니다. 설치에 대한 정보는 전송되지 않습니다. 끄려면 **노드 → 업데이트 및 등록**의 **새 버전**을 사용하십시오.

**제거**하면 프로그램, 서비스, 방화벽 규칙이 삭제됩니다. 녹화, 구성, `%ProgramData%\LarisVMS`는 유지됩니다.

## 소스에서 설치

개발자는 리포지토리에서 빌드하여 PowerShell로 설치할 수도 있습니다. `install-web.ps1`(서버)과 `build-node.ps1` / `build-proxy.ps1`이 만든 패키지 안의 `install-node.ps1` / `install-proxy.ps1` 스크립트를 사용합니다. `build-installers.ps1`은 세 개의 MSI를 빌드합니다. MSI는 스크립트로 설치된 컴퓨터를 그 자리에서 업그레이드하며 설정, 등록, 직접 스트리밍 엔드포인트와 그 방화벽 규칙을 유지합니다.

## 인증서

`appsettings.Production.json`의 `Kestrel:Certificates:Default:Path`와 `:Password`가 `.pfx` 파일을 가리킵니다. 서버는 60초마다 이를 확인하고 갱신된 인증서를 다시 시작 없이 교체합니다.
