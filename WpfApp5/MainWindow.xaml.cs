using KakaoPcLogger.Interop;
using KakaoPcLogger.Models;
using KakaoPcLogger.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
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
        private readonly string _dbPath;

        private long _captureCount;
        private int _rrIndex;
        private string? _currentViewKey;
        private ChatEntry? _sendTarget;

        private TaskbarFlashWatcher? _flashWatcher;

        // 윈도우별 쿨다운/재진입 방지
        private readonly Dictionary<IntPtr, DateTime> _lastCaptureUtcByHwnd = new();
        private readonly HashSet<IntPtr> _inProgress = new();

        // 캡처 쿨다운 (필요에 맞게 조정: 5~10초 권장)
        private static readonly TimeSpan CaptureCooldown = TimeSpan.FromSeconds(8);


        public MainWindow()
        {
            InitializeComponent();

            _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "kakao_chat_v2.db");
            _captureService = new ChatCaptureService(_windowInteractor, new ClipboardService(), _dbPath, _scanner);
            _sendService = new ChatSendService(_windowInteractor);

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

            _timer.Tick += OnTick;
            _timer.Interval = TimeSpan.FromMilliseconds(3000);

            LvChats.MouseDoubleClick += OnChatDoubleClick;

            ScanChats();
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
            // 같은 창에 대해 과도한 중복 캡처 방지
            var now = DateTime.UtcNow;

            if (_lastCaptureUtcByHwnd.TryGetValue(hwnd, out var last) &&
                (now - last) < CaptureCooldown)
            {
                // 쿨다운 범위면 무시
                // AppendLog($"[FLASH] skip (cooldown) hwnd=0x{hwnd.ToInt64():X}");
                return;
            }

            // 재진입 방지
            // 즉, 이미 캡처 중이면 스킵
            if (_inProgress.Contains(hwnd))
                return;

            _inProgress.Add(hwnd);
            _lastCaptureUtcByHwnd[hwnd] = now; // 먼저 찍어 중복 트리거 억제

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
                CaptureOne(entry, reopenAfterCapture: true);

                // 성공적으로 캡처했으면 최종 시각 갱신(선반영했지만 성공 타이밍으로 다시 박고 싶다면)
                _lastCaptureUtcByHwnd[hwnd] = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                AppendLog($"[FLASH 오류] {ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _inProgress.Remove(hwnd);
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

            BtnStartFlash.IsEnabled = true;
            BtnStopFlash.IsEnabled = false;
            TxtStatus.Text = "감시 중지";
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

        private void CaptureOne(ChatEntry entry, bool reopenAfterCapture = false)
        {
            IntPtr oldParentHwnd = entry.ParentHwnd;
            string oldLogKey = _chatLogManager.GetKey(entry);

            var result = _captureService.Capture(entry, reopenAfterCapture);

            if (!string.IsNullOrEmpty(result.Warning))
            {
                AppendLog(result.Warning);
                if (!result.Success)
                {
                    return;
                }
            }

            if (result.ReplacementEntry is not null)
            {
                UpdateEntryHandles(entry, result.ReplacementEntry, oldParentHwnd, oldLogKey);
            }
            else if (reopenAfterCapture)
            {
                _lastCaptureUtcByHwnd.Remove(oldParentHwnd);
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
                return;
            }

            string text = result.ClipboardText ?? string.Empty;
            var now = DateTime.Now;

            _captureCount++;
            TxtCount.Text = _captureCount.ToString();
            TxtTimestamp.Text = now.ToString("HH:mm:ss");

            AppendChatLog(entry, $"[#{_captureCount} {now:HH:mm:ss}] --- 캡처 시작 --- [{entry.Title}] {entry.HwndHex}\n");
            AppendChatLog(entry, text.EndsWith("\n", StringComparison.Ordinal) ? text : text + "\n");
            AppendChatLog(entry, $"[#{_captureCount}] --- 캡처 끝 ---\n");
        }

        private void UpdateEntryHandles(ChatEntry entry, ChatEntry replacement, IntPtr oldParentHwnd, string oldLogKey)
        {
            entry.ParentHwnd = replacement.ParentHwnd;
            entry.Hwnd = replacement.Hwnd;
            entry.ClassName = replacement.ClassName;
            entry.Pid = replacement.Pid;
            entry.Title = replacement.Title;

            _chatLogManager.ReplaceKey(oldLogKey, entry);

            if (_currentViewKey == oldLogKey)
            {
                _currentViewKey = _chatLogManager.GetKey(entry);
                if (_chatLogManager.TryGet(_currentViewKey, out var updatedLog))
                {
                    SetLog(updatedLog);
                }
            }

            _lastCaptureUtcByHwnd.Remove(oldParentHwnd);
            _lastCaptureUtcByHwnd[entry.ParentHwnd] = DateTime.UtcNow;
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
            if (string.IsNullOrEmpty(line))
            {
                return;
            }

            const int maxChars = 1_000_000;
            if (TxtLog.Text.Length > maxChars)
            {
                TxtLog.Clear();
            }

            TxtLog.AppendText(line);
            if (!line.EndsWith("\n", StringComparison.Ordinal))
            {
                TxtLog.AppendText(Environment.NewLine);
            }

            if (ChkAutoScroll.IsChecked == true)
            {
                TxtLog.ScrollToEnd();
            }
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
    }
}
