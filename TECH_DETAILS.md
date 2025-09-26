# 카카오톡 PC 메시지 송수신 기술 개요

## 개요
이 프로젝트는 카카오톡 PC 클라이언트의 채팅창을 직접 조작하여 메시지를 수집하고 전송합니다. WPF 애플리케이션 안에서 Win32 API를 P/Invoke로 호출해 카카오톡 창을 제어하고, 클립보드와 SQLite 데이터베이스를 활용해 메시지를 가공·보관합니다.

## 채팅 창 식별
- `ChatWindowScanner`는 `EnumWindows`와 `EnumChildWindows` 같은 Win32 열거 API를 사용해 카카오톡 프로세스 내의 채팅창과 하위 리스트 컨트롤을 찾습니다.【F:WpfApp5/Services/ChatWindowScanner.cs†L12-L86】
- 각 창의 핸들과 클래스 이름, 제목, 프로세스 ID를 수집해 `ChatEntry` 모델로 정리합니다.【F:WpfApp5/Services/ChatWindowScanner.cs†L39-L80】【F:WpfApp5/Models/ChatEntry.cs†L1-L28】

## 수신(캡처) 경로
1. **창 활성화 및 복사**: `ChatWindowInteractor.ActivateAndCopy`가 부모 창을 활성화하고 리스트 컨트롤 전체를 선택·복사합니다. 이때 `SetForegroundWindow`, `WM_ACTIVATE`, `WM_LBUTTONDOWN/UP`, `WM_KEYDOWN/UP` 등 윈도우 메시지를 직접 보냅니다.【F:WpfApp5/Services/ChatWindowInteractor.cs†L22-L86】【F:WpfApp5/Interop/NativeMethods.cs†L10-L62】【F:WpfApp5/Interop/NativeConstants.cs†L4-L24】
2. **클립보드 읽기**: `ClipboardService`는 WPF의 `System.Windows.Clipboard`를 반복적으로 호출해 텍스트를 안전하게 읽어 옵니다.【F:WpfApp5/Services/ClipboardService.cs†L1-L24】
3. **파싱 및 저장**: `ChatCaptureService`는 복사된 텍스트를 `ChatParser`로 파싱하고, SQLite 기반의 `ChatDatabase`에 메시지를 저장합니다. 데이터베이스는 `Microsoft.Data.Sqlite`를 이용해 생성되며, 채팅별로 고유 해시를 부여해 중복 저장을 방지합니다.【F:WpfApp5/Services/ChatCaptureService.cs†L1-L76】【F:WpfApp5/Data/ChatDatabase.cs†L1-L131】

## 송신 경로
1. **입력 컨트롤 탐색**: `ChatWindowInteractor.TrySendMessage`는 채팅창의 하위 컨트롤을 열거하여 `RICHEDIT50W`와 같은 다중 행 편집 컨트롤을 찾습니다.【F:WpfApp5/Services/ChatWindowInteractor.cs†L38-L110】
2. **텍스트 입력**: 찾은 컨트롤에 포커스를 주고, `WM_SETTEXT`로 메시지를 채워 넣은 뒤 캐럿을 끝으로 이동합니다. 필요할 때는 기존 내용을 `Ctrl+A`→`Backspace` 조합으로 지웁니다.【F:WpfApp5/Services/ChatWindowInteractor.cs†L44-L110】【F:WpfApp5/Interop/NativeConstants.cs†L7-L21】
3. **전송 트리거**: `Enter` 키에 해당하는 `WM_KEYDOWN/CHAR/KEYUP` 메시지를 순서대로 보내 카카오톡이 메시지를 전송하도록 유도합니다.【F:WpfApp5/Services/ChatWindowInteractor.cs†L112-L148】
4. **서비스 래퍼**: `ChatSendService`는 입력 컨트롤 클래스 이름을 주입할 수 있는 래퍼로, 실제 전송 로직은 모두 `ChatWindowInteractor`가 담당합니다.【F:WpfApp5/Services/ChatSendService.cs†L1-L17】

## 보조 기능
- `ChatWindowInteractor`는 `AttachThreadInput`과 키보드 상태 조작을 이용해 다른 스레드의 윈도우에 안전하게 키 입력을 전달합니다.【F:WpfApp5/Services/ChatWindowInteractor.cs†L69-L148】【F:WpfApp5/Interop/NativeMethods.cs†L34-L62】
- `ChatLogManager`는 캡처 및 송신 결과 로그를 메모리에 누적해 UI가 다시 접근할 수 있도록 합니다.【F:WpfApp5/Services/ChatLogManager.cs†L1-L55】

이와 같이 프로젝트는 Win32 메시지 전송과 클립보드·데이터베이스 처리를 결합해 카카오톡 PC용 메시지 송수신을 자동화합니다.
