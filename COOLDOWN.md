# 쿨다운 시스템 개요

`KakaoTalk-PC-Exporter`의 자동 캡처는 작업표시줄에서 발생하는 Flash/Redraw 신호(HSHELL_FLASH, HSHELL_REDRAW)를 받아 해당 채팅방을 바로 캡처하는 구조로 되어 있습니다. 이때 동일한 창에서 짧은 시간 안에 반복적으로 신호가 들어오면 중복 캡처가 과도하게 발생할 수 있기 때문에, `MainWindow` 내부에 "쿨다운 시스템"이 구현되어 있습니다. 이 문서는 그 동작 원리와 커스터마이징 방법을 설명합니다.

## 구성 요소

- **`TaskbarFlashWatcher`** : WPF 창을 `RegisterShellHookWindow`에 등록하여 카카오톡 프로세스에서 발생하는 작업표시줄 Flash/Redraw 이벤트를 수신하고, 감지된 창 핸들(`hwnd`)을 `OnSignal` 이벤트로 전달합니다.【F:WpfApp5/Services/TaskbarFlashWatcher.cs†L12-L77】
- **`MainWindow`** : Flash 신호를 받아 실제 캡처를 수행하며, 캡처 중복을 제어하기 위해 다음의 자료구조를 유지합니다.【F:WpfApp5/MainWindow.xaml.cs†L16-L42】
  - `_lastCaptureUtcByHwnd` : 창 핸들을 키로 사용하여 마지막 캡처 시각(UTC)을 기록하는 사전
  - `_inProgress` : 특정 창에 대한 캡처 작업이 아직 끝나지 않았는지를 나타내는 집합
  - `CaptureCooldown` : 한 창에서 연속 캡처 사이에 최소로 유지해야 하는 대기 시간(기본값 8초)

## 이벤트 흐름과 쿨다운 동작

1. `StartCaptureFlash`가 호출되면 `TaskbarFlashWatcher`가 시작되고, Flash/Redraw 이벤트를 수신할 때마다 `MainWindow.OnFlashSignal`이 실행됩니다.【F:WpfApp5/MainWindow.xaml.cs†L269-L288】【F:WpfApp5/Services/TaskbarFlashWatcher.cs†L34-L77】
2. `OnFlashSignal`은 먼저 현재 시각을 얻은 뒤, 해당 창(`hwnd`)의 마지막 캡처 시각과 비교합니다. 마지막 시각이 존재하고 `CaptureCooldown`(8초)보다 짧은 간격으로 호출되면 이번 신호는 무시됩니다.【F:WpfApp5/MainWindow.xaml.cs†L121-L132】
3. 동시에 `_inProgress` 집합을 확인하여, 이미 동일한 창에 대한 캡처가 진행 중이라면 재진입을 차단하고 반환합니다.【F:WpfApp5/MainWindow.xaml.cs†L134-L137】
4. 위 두 조건을 통과하면 `_inProgress`에 창 핸들을 추가하고, `_lastCaptureUtcByHwnd`에 현재 시각을 미리 기록하여 연속 신호에 대한 중복 트리거를 억제한 뒤 본격적인 캡처 절차(`CaptureOne`)를 수행합니다.【F:WpfApp5/MainWindow.xaml.cs†L139-L185】
5. 캡처가 정상적으로 끝나면 마지막 캡처 시각을 최신 값으로 덮어쓰고, `finally` 블록에서 `_inProgress` 집합에서 창 핸들을 제거하여 다음 신호를 받을 수 있도록 합니다.【F:WpfApp5/MainWindow.xaml.cs†L187-L196】

## 커스터마이징 팁

- **쿨다운 시간 조정** : `CaptureCooldown` 상수 값을 변경하면 창별 최소 대기 시간을 쉽게 조정할 수 있습니다. 예를 들어, 보다 민감한 감시가 필요하면 5초, 안정성을 중시하면 10초 이상으로 설정할 수 있습니다.【F:WpfApp5/MainWindow.xaml.cs†L41-L42】
- **쿨다운 초기화** : 필요에 따라 `_lastCaptureUtcByHwnd`를 비우면 모든 창에 대한 쿨다운이 즉시 해제됩니다. 현재 구현에서는 수동 초기화 UI는 없지만, `StartCaptureFlash`/`StopCaptureFlash` 토글 시 사전을 함께 초기화하도록 코드를 추가하면 됩니다.
- **재진입 제어 강화** : `_inProgress`는 윈도우별 단일 캡처만 허용하지만, 전역적인 동시 실행 수를 제한하고 싶다면 별도의 카운터를 추가하거나 `SemaphoreSlim`을 사용하는 방식으로 확장할 수 있습니다.

## 주의 사항

- 쿨다운은 작업표시줄 Flash 신호를 기점으로 동작하므로, 카카오톡 창이 실제로 알림을 보내지 않는 경우에는 트리거되지 않습니다. Round Robin 타이머(`OnTick`) 기반 캡처에는 쿨다운이 적용되지 않습니다.
- `TaskbarFlashWatcher` 내부에 준비된 `HashSet<int> _pidFilter` 및 `_debounce` 필드는 현재 사용되지 않으므로, 향후 다중 프로세스 지원이나 추가적인 디바운싱 로직이 필요하다면 여기를 확장해 활용할 수 있습니다.【F:WpfApp5/Services/TaskbarFlashWatcher.cs†L17-L21】

이와 같이 쿨다운 시스템은 창별 마지막 캡처 시각과 재진입 플래그를 결합하여 과도한 중복 캡처를 방지하고, 안정적인 자동 캡처 흐름을 보장합니다.
