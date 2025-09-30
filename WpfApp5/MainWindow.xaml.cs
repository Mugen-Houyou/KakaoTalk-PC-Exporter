using KakaoPcLogger.Interop;
using KakaoPcLogger.Models;
using KakaoPcLogger.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using System.Globalization;  // 시간 파싱에 사용
using WpfApp5.Services;

namespace KakaoPcLogger
{
    public partial class MainWindow : Window
    {
        private const string TargetProcessName = "KakaoTalk";
        private const string KakaoChatListClass = "EVA_VH_ListControl_Dblclk";

        private readonly ObservableCollection<ChatEntry> _chats = new();
        private readonly DispatcherTimer _timer = new();
        private readonly ChatLogManager _chatLogManager = new();
        private readonly ChatWindowScanner _scanner = new(TargetProcessName, KakaoChatListClass);
        private readonly ChatWindowInteractor _windowInteractor = new();
        private readonly ChatCaptureService _captureService;
        private readonly ChatSendService _sendService;
        private readonly RestApiService? _restApiService;
        private readonly WebhookNotificationService _webhookService;
        private readonly string _dbPath;

        private long _captureCount;
        private int _rrIndex;
        private string? _currentViewKey;
        private ChatEntry? _sendTarget;

        private TaskbarFlashWatcher? _flashWatcher;

        // 리프레시 서비스
        private readonly RefreshService _refreshService;

        // 리프레시 작업 수행 시 가드
        // 리프레시 스케줄러/상태
        private readonly DispatcherTimer _refreshTimer = new();
        private DateTime _lastRefreshDate = DateTime.MinValue;   // 하루 1회 실행 제어
        private volatile bool _refreshInProgress = false;        // 리프레시 중 가드

        // 윈도우별 쿨다운/재진입 방지
        private readonly Dictionary<IntPtr, DateTime> _lastCaptureUtcByHwnd = new();
        private readonly HashSet<IntPtr> _inProgress = new();

        // FLASH 큐 처리용
        private readonly object _flashQueueLock = new();
        private readonly Queue<(IntPtr hwnd, int code)> _flashQueue = new();
        private readonly HashSet<IntPtr> _queuedFlashTargets = new();
        private bool _isProcessingFlashQueue;

        // 캡처 쿨다운 (필요에 맞게 조정: 5~10초 권장)
        private static readonly TimeSpan CaptureCooldown = TimeSpan.FromSeconds(8);

        public MainWindow()
        {
            InitializeComponent();

            _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "kakao_chat_v2.db");
            _captureService = new ChatCaptureService(_windowInteractor, new ClipboardService(), _dbPath, _scanner);
            _sendService = new ChatSendService(_windowInteractor);

            var (webhookEndpoint, webhookSource, webhookError) = ResolveWebhookEndpoint();
            _webhookService = new WebhookNotificationService(webhookEndpoint, AppendLog);
            if (!string.IsNullOrEmpty(webhookError))
            {
                AppendLog(webhookError);
            }

            if (webhookEndpoint is not null)
            {
                string sourceText = string.IsNullOrEmpty(webhookSource) ? "(수동 설정)" : webhookSource;
                AppendLog($"[Webhook] 엔드포인트 사용: {webhookEndpoint} ← {sourceText}");
            }
            else
            {
                AppendLog("[Webhook] 엔드포인트가 설정되지 않아 전송 기능이 비활성화되었습니다.");
            }

            RestApiService? restApiService = null;
            try
            {
                restApiService = new RestApiService("http://localhost:5010/", _dbPath);
                restApiService.Log += AppendLog;
                restApiService.Start();
            }
            catch (Exception ex)
            {
                AppendLog($"[REST] 서비스 시작 실패: {ex.Message}");
            }

            _restApiService = restApiService;

            LvChats.ItemsSource = _chats;
            LvChats.SelectionChanged += OnChatSelectionChanged;

            BtnScan.Click += (_, __) => ScanChats();

            // 변경: FLASH & RR 각각
            BtnStartFlash.Click += (_, __) => StartCaptureFlash();
            BtnStopFlash.Click += (_, __) => StopCaptureFlash();

            BtnStartRr.Click += (_, __) => StartCaptureRr();
            BtnStopRr.Click += (_, __) => StopCaptureRr();

            BtnClear.Click += (_, __) =>
            {
                TxtLog.Clear();
                _captureCount = 0;
                TxtCount.Text = "0";
            };
            BtnCopyLog.Click += (_, __) =>
            {
                try
                {
                    Clipboard.SetText(TxtLog.Text);
                }
                catch (Exception ex)
                {
                    AppendLog($"[Clipboard] Copy failed: {ex.Message}");
                }
            };

            ChkSelectAll.Checked += (_, __) => SetAllSelection(true);
            ChkSelectAll.Unchecked += (_, __) => SetAllSelection(false);

            BtnSend.Click += (_, __) => SendComposer();
            BtnClearComposer.Click += (_, __) => ClearComposer();
            TxtComposer.TextChanged += OnComposerTextChanged;
            TxtComposer.PreviewKeyDown += OnComposerPreviewKeyDown;

            UpdateSendTargetLabel();
            UpdateComposerCount();

            // === RefreshService 초기화 ===
            _refreshService = new RefreshService(TargetProcessName, _scanner, new ClipboardService());

            // 로그 연결 (GUI 로그에 그대로 출력)
            _refreshService.Log += AppendLog;

            // 새 창으로 교체 콜백 (동일 타이틀의 HWND 업데이트)
            _refreshService.OnReopened += (oldTitle, reopened) =>
            {
                for (int i = 0; i < _chats.Count; i++)
                {
                    if (string.Equals(_chats[i].Title, oldTitle, StringComparison.Ordinal))
                    {
                        _chats[i].ParentHwnd = reopened.ParentHwnd;
                        _chats[i].Hwnd = reopened.Hwnd;
                    }
                }
            };

            // 사용자가 시간 입력칸을 바꾸면 즉시 반영 (포커스 잃을 때 등)
            TxtRefreshTime.LostFocus += (_, __) =>
            {
                if (!_refreshService.TryParseScheduledTime(TxtRefreshTime.Text))
                    AppendLog("[리프레시] 시간 파싱 실패. 예: 04:00");
            };

            // 지금 리프레시 버튼
            BtnRefreshNow.Click += async (_, __) =>
            {
                var titles = GetRefreshTargetTitles();
                await _refreshService.RunAsync(titles, "사용자 요청");
            };

            // 스케줄러: 30초마다 HH:mm에 맞으면 실행
            _refreshTimer.Interval = TimeSpan.FromSeconds(30);
            _refreshTimer.Tick += async (_, __) =>
            {
                // FLASH 방식이 켜진 상태에서만 스케줄 동작 (요구사항: FLASH 한정)
                if (_flashWatcher is null) return;

                // 선택된 항목(= FLASH 범위)을 타겟으로
                await _refreshService.TickScheduleAsync(GetRefreshTargetTitles);
            };
            _refreshTimer.Start();

            // 초기 스케줄 시간 반영 (텍스트에 값이 있으면)
            _refreshService.TryParseScheduledTime(TxtRefreshTime.Text);

            _timer.Tick += OnTick;
            _timer.Interval = TimeSpan.FromMilliseconds(3000);

            LvChats.MouseDoubleClick += OnChatDoubleClick;

            ScanChats();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            _restApiService?.Dispose();
            _webhookService.Dispose();
        }

        private void OnChatDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LvChats.SelectedItem is ChatEntry entry)
            {
                SetSendTarget(entry);

                string key = _chatLogManager.GetKey(entry);
                _currentViewKey = key;

                if (_chatLogManager.TryGet(key, out var text))
                {
                    SetLog(text);
                }
                else
                {
                    SetLog($"[{entry.Title}]의 로그가 비어 있음");
                }
            }
        }
        private void OnFlashSignal(IntPtr hwnd, int code)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => OnFlashSignal(hwnd, code)));
                return;
            }

            // 리프레시 작업 중일 경우 리턴
            if (_refreshInProgress)
                return;

            // 쿨다운/재진입 방지
            // 같은 창에 대해 과도한 중복 캡처 방지
            var now = DateTime.UtcNow;

            if (_lastCaptureUtcByHwnd.TryGetValue(hwnd, out var last) &&
                (now - last) < CaptureCooldown)
            {
                // 쿨다운 범위면 무시
                // AppendLog($"[FLASH] skip (cooldown) hwnd=0x{hwnd.ToInt64():X}");
                return;
            }

            lock (_flashQueueLock)
            {
                // 재진입 방지: 이미 처리 중이거나 큐 대기 중이면 무시
                if (_inProgress.Contains(hwnd) || _queuedFlashTargets.Contains(hwnd))
                    return;

                _flashQueue.Enqueue((hwnd, code));
                _queuedFlashTargets.Add(hwnd);
                _lastCaptureUtcByHwnd[hwnd] = now; // 먼저 찍어 중복 트리거 억제

                if (!_isProcessingFlashQueue)
                {
                    _isProcessingFlashQueue = true;
                    Dispatcher.BeginInvoke(new Action(ProcessFlashQueue));
                }
            }
        }

        private void ProcessFlashQueue()
        {
            while (true)
            {
                (IntPtr hwnd, int code) workItem;

                lock (_flashQueueLock)
                {
                    if (_flashQueue.Count == 0)
                    {
                        _isProcessingFlashQueue = false;
                        return;
                    }

                    workItem = _flashQueue.Dequeue();
                    _queuedFlashTargets.Remove(workItem.hwnd);
                    _inProgress.Add(workItem.hwnd);
                }

                try
                {
                    HandleFlashCapture(workItem.hwnd, workItem.code);
                }
                finally
                {
                    lock (_flashQueueLock)
                    {
                        _inProgress.Remove(workItem.hwnd);
                    }
                }
            }
        }

        private void HandleFlashCapture(IntPtr hwnd, int code)
        {
            _ = code; // 현재는 FLASH/RR 코드 구분 없이 hwnd 기반 처리

            if (_refreshInProgress)
            {
                return;
            }

            try
            {
                // 기존 매칭/캡처 로직
                var entries = _scanner.Scan(autoInclude: false);

                ChatEntry? entry =
                    entries.FirstOrDefault(e => e.ParentHwnd == hwnd) ??
                    entries.FirstOrDefault(e => e.Hwnd == hwnd);

                if (entry is null)
                {
                    // 보조 매칭: OWNER → ROOT 순으로 시도
                    IntPtr owner = NativeMethods.GetAncestor(hwnd, NativeConstants.GA_ROOTOWNER);
                    if (owner == IntPtr.Zero) owner = hwnd;

                    entry = entries.FirstOrDefault(e =>
                    {
                        var eo = NativeMethods.GetAncestor(e.ParentHwnd, NativeConstants.GA_ROOTOWNER);
                        if (eo == IntPtr.Zero) eo = e.ParentHwnd;
                        return eo == owner;
                    });

                    if (entry is null)
                    {
                        IntPtr root = NativeMethods.GetAncestor(hwnd, NativeConstants.GA_ROOT);
                        if (root == IntPtr.Zero) root = hwnd;

                        entry = entries.FirstOrDefault(e =>
                        {
                            var er = NativeMethods.GetAncestor(e.ParentHwnd, NativeConstants.GA_ROOT);
                            if (er == IntPtr.Zero) er = e.ParentHwnd;
                            return er == root;
                        });
                    }
                }

                if (entry is null)
                {
                    AppendLog($"[FLASH] 매칭 실패(쿨다운 적용 중): target=0x{hwnd.ToInt64():X}");
                    return;
                }

                // 실제 캡처
                var result = CaptureOne(entry, triggeredByFlash: true);

                // 성공적으로 캡처했으면 최종 시각 갱신(선반영했지만 성공 타이밍으로 다시 박고 싶다면)
                if (result?.Success == true)
                {
                    _lastCaptureUtcByHwnd[hwnd] = DateTime.UtcNow;
                }
                }
            catch (Exception ex)
            {
                AppendLog($"[FLASH 오류] {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnChatSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (LvChats.SelectedItem is ChatEntry entry)
            {
                SetSendTarget(entry);
            }
        }

        private void ScanChats()
        {
            try
            {
                bool autoInclude = ChkAutoInclude.IsChecked == true;
                var found = _scanner.Scan(autoInclude);

                IntPtr previousTarget = _sendTarget?.Hwnd ?? IntPtr.Zero;

                _chats.Clear();
                ChatEntry? matchedTarget = null;
                foreach (var chat in found)
                {
                    _chats.Add(chat);
                    if (previousTarget != IntPtr.Zero && chat.Hwnd == previousTarget)
                    {
                        matchedTarget = chat;
                    }
                }

                SetSendTarget(matchedTarget);
                if (matchedTarget != null)
                {
                    LvChats.SelectedItem = matchedTarget;
                }
                else
                {
                    LvChats.SelectedItem = null;
                }

                TxtChatCount.Text = _chats.Count.ToString();
                UpdateSelectedCount();
                TxtStatus.Text = $"스캔 완료: {_chats.Count}개 발견";
            }
            catch (Exception ex)
            {
                AppendLog($"[오류][Scan] {ex.GetType().Name}: {ex.Message}");
            }
        }

        private void SetAllSelection(bool value)
        {
            foreach (var chat in _chats)
            {
                chat.IsSelected = value;
            }
            UpdateSelectedCount();
        }

        private void UpdateSelectedCount()
        {
            int selected = 0;
            foreach (var chat in _chats)
            {
                if (chat.IsSelected)
                {
                    selected++;
                }
            }
            TxtSelectedCount.Text = selected.ToString();
        }
        private void StartCaptureFlash()
        {
            // 리프레시 관련 최신 텍스트 반영
            _refreshService?.TryParseScheduledTime(TxtRefreshTime.Text);

            BtnStartFlash.IsEnabled = false;
            BtnStopFlash.IsEnabled = true;

            _flashWatcher = new TaskbarFlashWatcher(TargetProcessName);
            _flashWatcher.OnSignal += OnFlashSignal;
            _flashWatcher.Start(this);

            TxtStatus.Text = $"작업표시줄 FLASH 감시 시작 - {this.ToString()}";
        }

        private void StopCaptureFlash()
        {
            _flashWatcher?.Dispose();
            _flashWatcher = null;

            ClearFlashQueue();

            BtnStartFlash.IsEnabled = true;
            BtnStopFlash.IsEnabled = false;
            TxtStatus.Text = "감시 중지";
        }

        private void ClearFlashQueue()
        {
            lock (_flashQueueLock)
            {
                _flashQueue.Clear();
                _queuedFlashTargets.Clear();
                _inProgress.Clear();
                _isProcessingFlashQueue = false;
            }
        }

        // Round Robin 캡처 (미사용)
        private void StartCaptureRr()
        {
            if (int.TryParse(TxtIntervalMs.Text.Trim(), out int ms) && ms >= 200)
            {
                _timer.Interval = TimeSpan.FromMilliseconds(ms);
            }
            else
            {
                _timer.Interval = TimeSpan.FromMilliseconds(3000);
            }

            _rrIndex = 0;
            BtnStartRr.IsEnabled = false;
            BtnStopRr.IsEnabled = true;
            TxtStatus.Text = "캡처 중…";
            _timer.Start();
        }

        // Round Robin 캡처 중지 (미사용)
        private void StopCaptureRr()
        {
            _timer.Stop();
            BtnStartRr.IsEnabled = true;
            BtnStopRr.IsEnabled = false;
            TxtStatus.Text = "대기 중";
        }

        private void OnTick(object? sender, EventArgs e)
        {
            try
            {
                var selected = new List<ChatEntry>();
                foreach (var chat in _chats)
                {
                    if (chat.IsSelected)
                    {
                        selected.Add(chat);
                    }
                }

                if (selected.Count == 0)
                {
                    TxtStatus.Text = "선택된 채팅방이 없습니다.";
                    return;
                }

                if (ChkRoundRobin.IsChecked == true)
                {
                    if (_rrIndex >= selected.Count)
                    {
                        _rrIndex = 0;
                    }

                    CaptureOne(selected[_rrIndex]);
                    _rrIndex = (_rrIndex + 1) % selected.Count;
                }
                else
                {
                    CaptureOne(selected[0]);
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[오류][Tick] {ex.GetType().Name}: {ex.Message}");
            }
        }

        private ChatCaptureResult? CaptureOne(ChatEntry entry, bool triggeredByFlash = false)
        {
            var result = _captureService.Capture(entry);

            if (!string.IsNullOrEmpty(result.Warning))
            {
                AppendLog(result.Warning);
                if (!result.Success)
                {
                    return result;
                }
            }

            if (!string.IsNullOrEmpty(result.DbMessage))
            {
                SetChatLog(entry, result.DbMessage);
            }

            if (!string.IsNullOrEmpty(result.DbError))
            {
                AppendLog(result.DbError);
            }

            if (!result.Success)
            {
                return result;
            }

            string text = result.ClipboardText ?? string.Empty;
            var now = DateTime.Now;

            _captureCount++;
            TxtCount.Text = _captureCount.ToString();
            TxtTimestamp.Text = now.ToString("HH:mm:ss");

            AppendChatLog(entry, $"[#{_captureCount} {now:HH:mm:ss}] --- 캡처 시작 --- [{entry.Title}] {entry.HwndHex}\n");
            AppendChatLog(entry, text.EndsWith("\n", StringComparison.Ordinal) ? text : text + "\n");
            AppendChatLog(entry, $"[#{_captureCount}] --- 캡처 끝 ---\n");

            if (triggeredByFlash && result.NewMessages.Count > 0)
            {
                _ = _webhookService.NotifyNewMessagesAsync(entry.Title, result.NewMessages);
            }

            return result;
        }

        private void AppendChatLog(ChatEntry entry, string line)
        {
            _chatLogManager.Append(entry, line);

            string key = _chatLogManager.GetKey(entry);
            if (_currentViewKey == key && _chatLogManager.TryGet(key, out var text))
            {
                SetLog(text);
            }
        }

        private void SetChatLog(ChatEntry entry, string text)
        {
            _chatLogManager.Set(entry, text);

            string key = _chatLogManager.GetKey(entry);
            if (_currentViewKey == key && _chatLogManager.TryGet(key, out var logText))
            {
                SetLog(logText);
            }
        }

        private void AppendLog(string line)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.Invoke(() => AppendLog(line));
                return;
            }
            if (string.IsNullOrEmpty(line)) return;
            const int maxChars = 1_000_000;
            if (TxtLog.Text.Length > maxChars) TxtLog.Clear();
            TxtLog.AppendText(line);
            if (!line.EndsWith("\n", StringComparison.Ordinal)) TxtLog.AppendText(Environment.NewLine);
            if (ChkAutoScroll.IsChecked == true) TxtLog.ScrollToEnd();
        }

        private void SetLog(string text)
        {
            text ??= string.Empty;

            const int maxChars = 1_000_000;
            if (text.Length > maxChars)
            {
                text = text[^maxChars..];
            }

            TxtLog.Text = text;

            if (ChkAutoScroll.IsChecked == true)
            {
                TxtLog.ScrollToEnd();
            }
        }

        private void SetSendTarget(ChatEntry? entry)
        {
            if (entry is null)
            {
                _sendTarget = null;
                UpdateSendTargetLabel();
                return;
            }

            foreach (var chat in _chats)
            {
                if (chat.Hwnd == entry.Hwnd)
                {
                    _sendTarget = chat;
                    UpdateSendTargetLabel();
                    return;
                }
            }

            _sendTarget = entry;
            UpdateSendTargetLabel();
        }

        private void UpdateSendTargetLabel()
        {
            if (_sendTarget is ChatEntry target)
            {
                TxtSendTarget.Text = $"{target.Title} ({target.HwndHex})";
            }
            else
            {
                TxtSendTarget.Text = "(미선택)";
            }
        }

        private ChatEntry? ResolveSendTarget()
        {
            if (_sendTarget is not null)
            {
                foreach (var chat in _chats)
                {
                    if (chat.Hwnd == _sendTarget.Hwnd)
                    {
                        if (!ReferenceEquals(chat, _sendTarget))
                        {
                            _sendTarget = chat;
                            UpdateSendTargetLabel();
                        }

                        return _sendTarget;
                    }
                }

                _sendTarget = null;
                UpdateSendTargetLabel();
            }

            if (LvChats.SelectedItem is ChatEntry selected)
            {
                SetSendTarget(selected);
                return _sendTarget;
            }

            return null;
        }

        private bool SendComposer()
        {
            string text = TxtComposer.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                AppendLog("[송신] 메시지가 비어 있어 전송하지 않았습니다.");
                return false;
            }

            var target = ResolveSendTarget();
            if (target is null)
            {
                AppendLog("[송신] 대상 채팅방이 선택되지 않았습니다.");
                return false;
            }

            try
            {
                if (!_sendService.TrySendMessage(target, text, out var error))
                {
                    AppendLog(error ?? "[송신] 전송 실패");
                    return false;
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[송신 오류] {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            AppendLog($"[송신] {target.Title} ({target.HwndHex}) ← {text.Length}자 메시지 전송");
            TxtComposer.Clear();

            if (ChkKeepFocusAfterSend.IsChecked == true)
            {
                Dispatcher.BeginInvoke(new Action(() => TxtComposer.Focus()), DispatcherPriority.ApplicationIdle);
            }

            return true;
        }

        private void ClearComposer()
        {
            TxtComposer.Clear();
            TxtComposer.Focus();
        }

        private static (Uri? Endpoint, string? Source, string? Error) ResolveWebhookEndpoint()
        {
            string? envValue = Environment.GetEnvironmentVariable("KAKAO_EXPORTER_WEBHOOK_URL");
            if (!string.IsNullOrWhiteSpace(envValue))
            {
                if (Uri.TryCreate(envValue, UriKind.Absolute, out var envUri))
                {
                    return (envUri, "환경 변수 KAKAO_EXPORTER_WEBHOOK_URL", null);
                }

                return (null, null, "[Webhook] 환경 변수 KAKAO_EXPORTER_WEBHOOK_URL 값을 URI로 해석하지 못했습니다.");
            }

            string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webhook-endpoint.txt");
            if (File.Exists(configPath))
            {
                string fileValue = File.ReadAllText(configPath).Trim();
                if (!string.IsNullOrEmpty(fileValue))
                {
                    if (Uri.TryCreate(fileValue, UriKind.Absolute, out var fileUri))
                    {
                        return (fileUri, $"파일 {configPath}", null);
                    }

                    return (null, null, $"[Webhook] 파일 {configPath}의 내용을 URI로 해석하지 못했습니다.");
                }
            }

            return (null, null, null);
        }

        private void OnComposerTextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateComposerCount();
        }

        private void OnComposerPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter && e.Key != Key.Return)
            {
                return;
            }

            ModifierKeys modifiers = Keyboard.Modifiers;
            bool hasCtrl = (modifiers & ModifierKeys.Control) == ModifierKeys.Control;
            bool hasShift = (modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
            bool hasAlt = (modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
            bool enterToSend = ChkEnterToSend.IsChecked == true;

            if (enterToSend)
            {
                if (!hasCtrl && !hasShift && !hasAlt)
                {
                    e.Handled = true;
                    SendComposer();
                }
            }
            else
            {
                if (hasCtrl && !hasShift && !hasAlt)
                {
                    e.Handled = true;
                    SendComposer();
                }
            }
        }

        private void UpdateComposerCount()
        {
            TxtComposerCount.Text = TxtComposer.Text.Length.ToString();
        }

        private void BtnClear_Click(object sender, RoutedEventArgs e)
        {
            TxtLog.Clear();
        }
        private TimeSpan GetScheduledRefreshTime()
        {
            // 기본값 04:00
            var text = (TxtRefreshTime.Text ?? "04:00").Trim();
            // HH:mm / H:mm 둘 다 허용
            if (TimeSpan.TryParseExact(text,
                                       new[] { @"hh\:mm", @"h\:mm" },
                                       CultureInfo.InvariantCulture,
                                       out var ts))
                return ts;

            return new TimeSpan(4, 0, 0);
        }

        //private async System.Threading.Tasks.Task RunRefreshAsync(string reason)
        //{
        //    if (_refreshInProgress) return;       // 동시 실행 금지
        //    _refreshInProgress = true;

        //    // 버튼/상태 잠금
        //    try
        //    {
        //        // UI 잠금: FLASH/RR 시작 버튼 비활성화, Stop만 남겨둬도 됨
        //        BtnStartFlash.IsEnabled = false;
        //        BtnStopFlash.IsEnabled = true;   // 감시는 켠 상태로 두되, 콜백에서 가드가 먹음
        //        BtnStartRr.IsEnabled = false;
        //        BtnStopRr.IsEnabled = false;

        //        TxtStatus.Text = $"[리프레시] 진행 중… ({reason})";
        //        AppendLog($"[리프레시] 시작: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({reason})");

        //        // --- 실제 리프레시 작업 자리 ---
        //        // TODO: 여기서 각 채팅방 윈도우를 닫고 다시 여세요.
        //        // 예시(의사코드):
        //        // var targets = _scanner.Scan(autoInclude:false);
        //        // foreach (var chat in targets) {
        //        //     NativeMethods.PostMessage(chat.ParentHwnd, NativeConstants.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        //        //     await Task.Delay(200);
        //        //     // 다시 열기: 좌측 리스트/검색/타이틀 매칭으로 재오픈 기능 구현 예정
        //        // }
        //        await System.Threading.Tasks.Task.Delay(500); // 임시: 최소 대기
        //                                                      // ----------------------------------

        //        _lastRefreshDate = DateTime.Now.Date;
        //        AppendLog($"[리프레시] 완료");
        //    }
        //    catch (Exception ex)
        //    {
        //        AppendLog($"[리프레시 오류] {ex.GetType().Name}: {ex.Message}");
        //    }
        //    finally
        //    {
        //        // 버튼/상태 복구
        //        BtnStartFlash.IsEnabled = (_flashWatcher is null); // 감시 중이 아니면 시작 가능
        //        BtnStopFlash.IsEnabled = (_flashWatcher is not null);

        //        BtnStartRr.IsEnabled = true;   // RR은 따로
        //        BtnStopRr.IsEnabled = false;

        //        TxtStatus.Text = (_flashWatcher is not null) ? "FLASH 감시 동작 중" : "대기 중";
        //        _refreshInProgress = false;
        //    }
        //}
        private IEnumerable<string> GetRefreshTargetTitles()
        {
            return _chats
                .Where(c => c.IsSelected && !string.IsNullOrWhiteSpace(c.Title))
                .Select(c => c.Title)
                .Distinct(StringComparer.Ordinal);
        }
    }
}
