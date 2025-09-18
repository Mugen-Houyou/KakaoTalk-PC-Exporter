using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using KakaoPcLogger.Models;
using KakaoPcLogger.Services;

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
        private readonly ChatCaptureService _captureService;
        private readonly string _dbPath;

        private long _captureCount;
        private int _rrIndex;
        private string? _currentViewKey;

        public MainWindow()
        {
            InitializeComponent();

            _dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "kakao_chat_v2.db");
            _captureService = new ChatCaptureService(new ChatWindowInteractor(), _dbPath);

            LvChats.ItemsSource = _chats;

            BtnScan.Click += (_, __) => ScanChats();
            BtnStart.Click += (_, __) => StartCapture();
            BtnStop.Click += (_, __) => StopCapture();
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

            _timer.Tick += OnTick;
            _timer.Interval = TimeSpan.FromMilliseconds(1500);

            LvChats.MouseDoubleClick += OnChatDoubleClick;

            ScanChats();
        }

        private void OnChatDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (LvChats.SelectedItem is ChatEntry entry)
            {
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

        private void ScanChats()
        {
            try
            {
                bool autoInclude = ChkAutoInclude.IsChecked == true;
                var found = _scanner.Scan(autoInclude);

                _chats.Clear();
                foreach (var chat in found)
                {
                    _chats.Add(chat);
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

        private void StartCapture()
        {
            if (int.TryParse(TxtIntervalMs.Text.Trim(), out int ms) && ms >= 200)
            {
                _timer.Interval = TimeSpan.FromMilliseconds(ms);
            }
            else
            {
                _timer.Interval = TimeSpan.FromMilliseconds(1500);
            }

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

        private void CaptureOne(ChatEntry entry)
        {
            var result = _captureService.Capture(entry);

            if (!string.IsNullOrEmpty(result.Warning))
            {
                AppendLog(result.Warning);
                if (!result.Success)
                {
                    return;
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
                return;
            }

            string text = result.CapturedText ?? string.Empty;
            var now = DateTime.Now;

            _captureCount++;
            TxtCount.Text = _captureCount.ToString();
            TxtTimestamp.Text = now.ToString("HH:mm:ss");

            AppendChatLog(entry, $"[#{_captureCount} {now:HH:mm:ss}] --- 캡처 시작 --- [{entry.Title}] {entry.HwndHex}\n");
            AppendChatLog(entry, text.EndsWith("\n", StringComparison.Ordinal) ? text : text + "\n");
            AppendChatLog(entry, $"[#{_captureCount}] --- 캡처 끝 ---\n");
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
    }
}
