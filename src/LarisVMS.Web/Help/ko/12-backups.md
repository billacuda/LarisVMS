# 백업

데이터베이스 백업과 그 외에 백업해야 하는 것을 설명합니다.

**설정 → 백업**은 LarisVMS 데이터베이스의 SQL Server 전체 백업을 일일 일정 또는 필요할 때 수행합니다. 자체 백업을 예약할 SQL Agent가 없는 SQL Express용입니다.

- **백업 디렉터리**: *SQL Server 서비스*가 쓸 수 있는 폴더이며 LarisVMS 설치 폴더 밖이어야 합니다. SQL Server가 다른 컴퓨터에 있으면 이 경로는 그 컴퓨터에 있어야 하며, 오래된 백업은 자동으로 정리되지 않습니다.
- **보관할 개수**: 보관할 백업의 수입니다(로컬 SQL Server만 해당).

복원은 LarisVMS 밖에서 SSMS 또는 `sqlcmd`로 수행합니다. 백업은 전체 백업만 지원합니다. SQL Express에서는 SIMPLE 복구 모델을 사용하십시오.

## MariaDB

MariaDB에서는 백업이 `mariadb-dump`로 만들어지며 압축된 `.sql.gz` 파일로 저장됩니다. `mariadb-dump`는 LarisVMS 서버에서 실행되므로, MariaDB 자체가 다른 컴퓨터에서 실행되더라도 MariaDB 클라이언트 도구가 거기에 설치되어 있어야 합니다. LarisVMS는 `C:\Program Files\MariaDB …`에서 이를 찾으며, Super Admin이 백업 페이지에서 `mariadb-dump.exe`의 경로를 설정할 수도 있습니다. LarisVMS가 서비스 자체의 권한으로 실행하므로, 이 경로는 이 서버에서 관리자만 변경할 수 있는 폴더(예: Program Files 아래)에 있어야 합니다.

- **백업 디렉터리**: LarisVMS 서버의 폴더(또는 공유)로, *LarisVMS 서비스*가 쓸 수 있어야 하며 설치 폴더 밖이어야 합니다. 오래된 백업은 MariaDB가 어디서 실행되든 항상 정리됩니다.
- 백업에는 녹화를 멈추지 않고 만든 하나의 일관된 스냅샷으로 전체 데이터베이스가 들어 있습니다.

`.sql.gz`의 압축을 풀고(예: 7-Zip) `mariadb` 클라이언트로 복원합니다. 이 파일은 데이터베이스가 없으면 만들고 테이블을 교체하므로, 먼저 LarisVMS Web 서비스를 중지하십시오.

```powershell
cmd /c "mariadb -u root -p < larisvms_20261026_030000.sql"
```

## 함께 백업할 것

- `%ProgramData%\LarisVMS\keys`의 **데이터 보호 키**. 카메라 비밀번호, 이메일 비밀, 노드 키는 이 키로 암호화되어 있습니다. 이 키가 없으면 복원한 데이터베이스에서 이를 복호화할 수 없습니다.
- 설치 폴더의 **`setup-generated.json`**. 데이터베이스 연결 문자열이 들어 있습니다. 그 비밀번호는 저장한 컴퓨터에서만 작동합니다. 새 컴퓨터에서는 설정 마법사를 다시 실행하고 복원한 데이터베이스를 지정하십시오.

녹화는 데이터베이스 백업에 포함되지 않습니다. 보관 볼륨이나 자체 저장소 백업으로 보호하십시오.
