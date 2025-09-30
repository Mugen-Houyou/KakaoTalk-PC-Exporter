# HttpListener URL ACL 문제 해결 가이드

`HttpListener`는 Windows의 HTTP.SYS를 사용하여 URL 프리픽스에 대한 리슨 권한을 관리합니다. 기본적으로 관리자 권한을 가진 프로세스만 등록되지 않은 URL을 열 수 있기 때문에, 일반 사용자 권한으로 실행하거나 커스텀 호스트/포트를 사용하면 `Access is denied`, `The process does not have access rights to this namespace` 와 같은 예외가 발생할 수 있습니다.

## 1. 관리자 권한으로 실행하기
가장 간단한 해결책은 애플리케이션을 관리자 권한으로 실행하는 것입니다. 그러나 장기적으로는 특정 URL 프리픽스에 대한 명시적인 ACL을 등록하는 것이 더 안전합니다.

## 2. netsh로 URL ACL 등록하기
Windows에서 URL ACL을 추가하려면 관리자 권한 PowerShell 또는 명령 프롬프트를 열고 다음 명령을 실행하세요.

```powershell
netsh http add urlacl url=http://+:5010/ user=DOMAIN\User
```

- `url=` 값은 허용하고 싶은 URL 프리픽스입니다. 강력 와일드카드(`+`)는 모든 호스트명을 허용합니다. 특정 호스트만 허용하려면 `http://mytesthost.com:5010/` 처럼 지정할 수 있습니다.
- `user=` 값은 해당 URL에 바인딩할 권한을 부여할 Windows 사용자 또는 그룹입니다. 로컬 사용자라면 `COMPUTERNAME\User`, Microsoft 계정이라면 `MicrosoftAccount\email@example.com` 형식을 사용하세요.

### HTTPS 프리픽스 예시
```powershell
netsh http add urlacl url=https://+:5011/ user=DOMAIN\User
```
HTTPS를 사용하려면 추가로 인증서 바인딩이 필요합니다.

## 3. 기존 ACL 확인 및 삭제
등록된 ACL 목록을 확인하려면 다음 명령을 사용합니다.

```powershell
netsh http show urlacl
```

더 이상 필요하지 않은 항목을 제거하려면 다음과 같이 삭제할 수 있습니다.

```powershell
netsh http delete urlacl url=http://+:5010/
```

## 4. 자주 발생하는 오류
| 오류 메시지 | 해결 방법 |
|-------------|------------|
| `Access is denied` | 해당 URL에 대한 ACL이 없거나 사용자가 권한이 없습니다. 관리자 권한 실행 또는 `netsh http add urlacl` 명령으로 권한을 부여하세요. |
| `The process does not have access rights to this namespace` | URL 프리픽스를 다른 프로세스가 사용 중이거나 권한이 없습니다. `netsh http show urlacl`로 확인 후 필요한 ACL을 추가하세요. |
| `Cannot create a file when that file already exists` | 이미 등록된 프리픽스입니다. 필요하다면 삭제 후 다시 추가하거나 다른 포트를 사용하세요. |

## 참고 사항
- `netsh` 명령은 관리자 권한으로 실행해야 합니다.
- REST API를 여러 호스트에서 리슨하도록 구성한 경우(`RestApi:Hosts`), 각 호스트/포트 조합에 대해 URL ACL을 등록해야 합니다. 강력 와일드카드(`+`) 프리픽스를 사용하면 한 번의 등록으로 모든 호스트를 허용할 수 있습니다.
- 회사 정책이나 보안 요구 사항에 따라 URL ACL 등록이 제한될 수 있습니다. 이 경우 IT 관리자와 상의하세요.
