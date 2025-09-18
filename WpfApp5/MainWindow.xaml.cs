using System;
using System.IO;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Data.Sqlite;

namespace KakaoPcLogger
{
    public partial class MainWindow : Window
    {
        // ====== 모델 ======
        public class ChatEntry : INotifyPropertyChanged
        {
            public IntPtr Hwnd { get; set; }
            public IntPtr ParentHwnd { get; set; }
            public string Title { get; set; } = "";
            public string ClassName { get; set; } = "";
            public int Pid { get; set; }

            private bool _isSelected;
            public bool IsSelected
            {
                get => _isSelected;
                set { _isSelected = value; OnPropertyChanged(nameof(IsSelected)); }
            }

            public string HwndHex => $"0x{Hwnd.ToInt64():X}";
            public event PropertyChangedEventHandler? PropertyChanged;
            private void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        private readonly ObservableCollection<ChatEntry> _chats = new ObservableCollection<ChatEntry>();

        // ====== 타이머/상태 ======
        private readonly DispatcherTimer _timer = new DispatcherTimer();
        private long _captureCount = 0;
        private int _rrIndex = 0; // 라운드로빈 인덱스

        // ====== 상수 ======
        private const string TargetProcessName = "KakaoTalk";
        private const string KakaoChatListClass = "EVA_VH_ListControl_Dblclk";

        private const int WM_ACTIVATE = 0x0006;
        private const int WA_ACTIVE = 1;
        private const int WM_KEYDOWN = 0x0100;
        private const int WM_KEYUP = 0x0101;
        private const int WM_SYSKEYDOWN = 0x0104;
        private const int WM_SYSKEYUP = 0x0105;
        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_LBUTTONUP = 0x0202;

        private const int VK_CONTROL = 0x11;
        private const int VK_A = 0x41;
        private const int VK_C = 0x43;
        private const int VK_ESCAPE = 0x1B;

        // 채팅방별 로그
        private readonly Dictionary<string, StringBuilder> _chatLogs = new();
        // 마지막으로 화면에 표시 중인 키(옵션)
        private string? _currentViewKey = null;
        // 키 생성 헬퍼
        private static string ChatKey(ChatEntry e) => $"{e.Title}|0x{e.Hwnd.ToInt64():X}";

        // ====== P/Invoke ======
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint uCode, uint uMapType);

        // 기존
        // [DllImport("user32.dll")]
        // private static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

        // 교체 (threadId 반환, out으로 프로세스 ID를 받음)
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        // 얘는 kernel32에서 가져와야 함
        [DllImport("kernel32.dll")]
        private static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

        [DllImport("user32.dll")]
        private static extern bool GetKeyboardState(byte[] lpKeyState);

        [DllImport("user32.dll")]
        private static extern bool SetKeyboardState(byte[] lpKeyState);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // 헬퍼
        private static IntPtr MakeLParam(ushort low, ushort high)
            => (IntPtr)((high << 16) | (low & 0xFFFF));
        private static IntPtr MakeKeyLParam(uint vk)
        {
            ushort scan = (ushort)MapVirtualKey(vk, 0);
            return MakeLParam(0, scan);
        }

        public MainWindow()
        {
            InitializeComponent();

            // 바인딩
            LvChats.ItemsSource = _chats;

            // 버튼/체크 이벤트
            BtnScan.Click += (_, __) => ScanChats();
            BtnStart.Click += (_, __) => StartCapture();
            BtnStop.Click += (_, __) => StopCapture();
            BtnClear.Click += (_, __) => { TxtLog.Clear(); _captureCount = 0; TxtCount.Text = "0"; };
            BtnCopyLog.Click += (_, __) =>
            {
                try { Clipboard.SetText(TxtLog.Text); }
                catch (Exception ex) { AppendLog($"[Clipboard] Copy failed: {ex.Message}"); }
            };
            ChkSelectAll.Checked += (_, __) => SetAllSelection(true);
            ChkSelectAll.Unchecked += (_, __) => SetAllSelection(false);

            // 타이머
            _timer.Tick += OnTick;
            _timer.Interval = TimeSpan.FromMilliseconds(1500);

            // 채팅방 더블클릭 바인딩
            LvChats.MouseDoubleClick += OnChatDoubleClick;

            // 최초 스캔
            ScanChats();
        }

        // 채팅방 더블클릭 시 액션
        private void OnChatDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (LvChats.SelectedItem is ChatEntry entry)
            {
                string key = ChatKey(entry);
                _currentViewKey = key;

                if (_chatLogs.TryGetValue(key, out var sb))
                    SetLog(sb.ToString());
                else
                    SetLog($"[{entry.Title}]의 로그가 비어 있음");
            }
        }

        // ====== 스캔 ======
        private void ScanChats()
        {
            try
            {
                var found = new ObservableCollection<ChatEntry>();

                EnumWindows((hTop, l) =>
                {
                    if (!IsWindow(hTop)) return true;

                    // 정확히 PID 얻기
                    uint pid;
                    uint tid = GetWindowThreadProcessId(hTop, out pid);

                    Process p;
                    try { p = Process.GetProcessById((int)pid); }
                    catch { return true; } // 접근 불가/이미 종료 등

                    if (!string.Equals(p.ProcessName, TargetProcessName, StringComparison.OrdinalIgnoreCase))
                        return true;

                    string parentTitle = GetWindowTextSafe(hTop);

                    // 자식 중 KakaoTalk 리스트 컨트롤만 수집
                    EnumChildWindows(hTop, (hChild, _) =>
                    {
                        string cls = GetClassNameSafe(hChild);
                        if (string.Equals(cls, KakaoChatListClass, StringComparison.Ordinal))
                        {
                            found.Add(new ChatEntry
                            {
                                Hwnd = hChild,
                                ParentHwnd = hTop,
                                Title = string.IsNullOrWhiteSpace(parentTitle) ? "(제목 없음/불명)" : parentTitle,
                                ClassName = cls,
                                Pid = (int)pid,
                                IsSelected = ChkAutoInclude.IsChecked == true // 새 창 자동 포함 옵션
                            });
                        }
                        return true;
                    }, IntPtr.Zero);

                    return true;
                }, IntPtr.Zero);

                _chats.Clear();
                foreach (var c in found) _chats.Add(c);

                TxtChatCount.Text = _chats.Count.ToString();
                UpdateSelectedCount();
                TxtStatus.Text = $"스캔 완료: {_chats.Count}개 발견";
            }
            catch (Exception ex)
            {
                AppendLog($"[오류][Scan] {ex.GetType().Name}: {ex.Message}");
            }
        }


        private string GetClassNameSafe(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }
        private string GetWindowTextSafe(IntPtr h)
        {
            var sb = new StringBuilder(512);
            GetWindowText(h, sb, sb.Capacity);
            return sb.ToString();
        }

        private void SetAllSelection(bool value)
        {
            foreach (var c in _chats) c.IsSelected = value;
            UpdateSelectedCount();
        }
        private void UpdateSelectedCount()
        {
            int sel = 0;
            foreach (var c in _chats) if (c.IsSelected) sel++;
            TxtSelectedCount.Text = sel.ToString();
        }

        // ====== 시작/중지 ======
        private void StartCapture()
        {
            if (int.TryParse(TxtIntervalMs.Text.Trim(), out int ms) && ms >= 200)
                _timer.Interval = TimeSpan.FromMilliseconds(ms);
            else
                _timer.Interval = TimeSpan.FromMilliseconds(1500);

            _rrIndex = 0;
            BtnStart.IsEnabled = false;
            BtnStop.IsEnabled = true;
            TxtStatus.Text = "캡처 중…";
            _timer.Start();
        }

        private void StopCapture()
        {
            _timer.Stop();
            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
            TxtStatus.Text = "대기 중";
        }

        // ====== 주기 작업 ======
        private void OnTick(object? sender, EventArgs e)
        {
            try
            {
                // 현재 선택된 채팅방 리스트
                var selected = new System.Collections.Generic.List<ChatEntry>();
                foreach (var c in _chats) if (c.IsSelected) selected.Add(c);

                if (selected.Count == 0)
                {
                    TxtStatus.Text = "선택된 채팅방이 없습니다.";
                    return;
                }

                // 라운드로빈이면 tick마다 하나씩, 아니면 첫 항목만
                if (ChkRoundRobin.IsChecked == true)
                {
                    if (_rrIndex >= selected.Count) _rrIndex = 0;
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

        private void CaptureOne(ChatEntry entry)
        {
            // 윈도우 유효성
            if (!IsWindow(entry.ParentHwnd) || !IsWindow(entry.Hwnd))
            {
                AppendLog($"[경고] 무효 핸들: {entry.Title} {entry.HwndHex}");
                return;
            }

            // 활성화/포커스 맞추기
            SendMessage(entry.ParentHwnd, WM_ACTIVATE, (IntPtr)WA_ACTIVE, IntPtr.Zero);
            SetForegroundWindow(entry.ParentHwnd);

            // Ctrl+A, Ctrl+C
            PressKeyCombo(entry.Hwnd, VK_CONTROL, VK_A, sysKey: false);
            System.Threading.Thread.Sleep(30);
            PressKeyCombo(entry.Hwnd, VK_CONTROL, VK_C, sysKey: false);

            // deselect
            DeselectList(entry.Hwnd);

            // 클립보드 텍스트 읽기
            string? text = ReadClipboardTextSafe();
            if (text is null)
            {
                AppendLog($"[경고] 클립보드 읽기 실패: {entry.Title}");
                return;
            }

            // 로그 적재 (SQLite)
            try
            {
                string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "kakao_chat_v2.db");
                ChatStorage.EnsureDatabase(dbPath);

                long chatId = ChatStorage.GetOrCreateChatId(dbPath, entry.Title);
                var parsed = ChatStorage.ParseRaw(text);

                if (parsed.Count > 0)
                {
                    ChatStorage.SaveMessages(dbPath, chatId, parsed);
                    SetChatLog(entry,$"[DB] 저장됨: {parsed.Count}건 ({entry.Title})\n");
                }
                else
                {
                    SetChatLog(entry, "[DB] 날짜 구분이 없어 저장 생략\n");
                }
            }
            catch (Exception ex)
            {
                AppendLog($"[DB 오류] {ex.GetType().Name}: {ex.Message}");
            }

            // 로그 적재 (GUI Window)
            _captureCount++;
            TxtCount.Text = _captureCount.ToString();
            TxtTimestamp.Text = DateTime.Now.ToString("HH:mm:ss");

            //SetLog($"[#{_captureCount} {DateTime.Now:HH:mm:ss}] --- 캡처 시작 --- [{entry.Title}] {entry.HwndHex}\n");
            //AppendLog(text);
            //AppendLog($"[#{_captureCount}] --- 캡처 끝 ---\n");

            AppendChatLog(entry, $"[#{_captureCount} {DateTime.Now:HH:mm:ss}] --- 캡처 시작 --- [{entry.Title}] 0x{entry.Hwnd.ToInt64():X}\n");
            AppendChatLog(entry, text + (text.EndsWith("\n") ? "" : "\n"));
            AppendChatLog(entry, $"[#{_captureCount}] --- 캡처 끝 ---\n");
        }

        // ====== 키 전송 (조합키/단일키) ======
        private void PressKeyCombo(IntPtr hwnd, int modifierVk, int keyVk, bool sysKey)
        {
            if (!IsWindow(hwnd)) return;

            // 대상/현재 스레드 묶기
            uint pid; // 필요하다면 사용
            uint targetTid = GetWindowThreadProcessId(hwnd, out pid);
            uint currTid = GetCurrentThreadId();

            AttachThreadInput(currTid, targetTid, true);

            var oldState = new byte[256];
            var newState = new byte[256];
            GetKeyboardState(oldState);
            Array.Copy(oldState, newState, 256);

            // modifier 눌림 상태
            newState[modifierVk] |= 0x80;
            SetKeyboardState(newState);

            int msgDown = sysKey ? WM_SYSKEYDOWN : WM_KEYDOWN;
            int msgUp = sysKey ? WM_SYSKEYUP : WM_KEYUP;

            IntPtr lparam = MakeKeyLParam((uint)keyVk);
            PostMessage(hwnd, msgDown, (IntPtr)keyVk, lparam);
            System.Threading.Thread.Sleep(10);
            PostMessage(hwnd, msgUp, (IntPtr)keyVk, (IntPtr)(lparam.ToInt64() | (1L << 30) | (1L << 31)));

            // 상태 원복
            Array.Copy(oldState, newState, 256);
            SetKeyboardState(newState);

            AttachThreadInput(currTid, targetTid, false);
        }

        private void PressSingleKey(IntPtr hwnd, int vk)
        {
            if (!IsWindow(hwnd)) return;
            IntPtr lp = MakeKeyLParam((uint)vk);
            PostMessage(hwnd, WM_KEYDOWN, (IntPtr)vk, lp);
            System.Threading.Thread.Sleep(8);
            PostMessage(hwnd, WM_KEYUP, (IntPtr)vk, (IntPtr)(lp.ToInt64() | (1L << 30) | (1L << 31)));
        }

        private void DeselectList(IntPtr hwndList)
        {
            //// ESC로 전체 선택 해제
            //PressSingleKey(hwndList, VK_ESCAPE);

            // 보조: 리스트 좌측 상단 클릭
            IntPtr pt = MakeLParam(30, 3);
            PostMessage(hwndList, WM_LBUTTONDOWN, (IntPtr)1, pt);
            PostMessage(hwndList, WM_LBUTTONUP, IntPtr.Zero, pt);
        }

        // ====== 클립보드 안전 읽기 ======
        private string? ReadClipboardTextSafe()
        {
            for (int i = 0; i < 6; i++)
            {
                try
                {
                    if (Clipboard.ContainsText())
                        return Clipboard.GetText();
                    return string.Empty;
                }
                catch
                {
                    System.Threading.Thread.Sleep(30);
                }
            }
            return null;
        }

        // ====== 로그 ======
        private void AppendLog(string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            const int maxChars = 1_000_000; // 1MB 정도
            if (TxtLog.Text.Length > maxChars)
                TxtLog.Clear();

            TxtLog.AppendText(line);
            if (!line.EndsWith("\n"))
                TxtLog.AppendText(Environment.NewLine);

            if (ChkAutoScroll.IsChecked == true)
                TxtLog.ScrollToEnd();
        }
        private void SetLog(string text)
        {
            // null → 빈 문자열 처리
            text ??= string.Empty;

            const int maxChars = 1_000_000; // 1MB 정도
            if (text.Length > maxChars)
            {
                // 너무 길면 맨 뒤쪽만 남기고 앞부분을 잘라냄
                text = text[^maxChars..];
            }

            TxtLog.Text = text;

            // 자동 스크롤 옵션이 켜져 있다면 끝으로 이동
            if (ChkAutoScroll.IsChecked == true)
                TxtLog.ScrollToEnd();
        }

        // 이거는 채팅방별 로그
        private void AppendChatLog(ChatEntry entry, string line)
        {
            if (string.IsNullOrEmpty(line)) return;

            string key = ChatKey(entry);
            if (!_chatLogs.TryGetValue(key, out var sb))
            {
                sb = new StringBuilder();
                _chatLogs[key] = sb;
            }

            // 용량 제한 (메모리 보호)
            const int maxChars = 1_000_000;
            if (sb.Length > maxChars)
                sb.Clear();

            if (sb.Length > 0 && !line.EndsWith("\n"))
                sb.AppendLine(line);
            else
                sb.Append(line);

            // 현재 화면에 이 채팅방이 보이는 중이면 즉시 반영
            if (_currentViewKey == key)
            {
                // 전체 갱신 부담 줄이려면 SetLog 대신 TxtLog.AppendText도 가능하지만,
                // 화면과 버퍼의 일관성을 위해 SetLog로 동기화
                SetLog(sb.ToString());
            }
        }
        private void SetChatLog(ChatEntry entry, string text)
        {
            if (entry == null) return;

            // null -> 빈 문자열
            text ??= string.Empty;

            string key = ChatKey(entry);

            // 용량 제한 (메모리 보호)
            const int maxChars = 1_000_000; // 1MB 정도
            if (text.Length > maxChars)
            {
                // 뒤쪽 최신 로그 위주로 남기기
                text = text[^maxChars..];
            }

            // 버퍼 교체
            var sb = new StringBuilder(text.Length + 64);
            sb.Append(text);
            _chatLogs[key] = sb;

            // 현재 화면에 이 채팅방이 보이는 중이면 즉시 반영
            if (_currentViewKey == key)
            {
                SetLog(sb.ToString());
            }
        }

    }
}
