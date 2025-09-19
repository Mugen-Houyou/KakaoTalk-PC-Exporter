using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using KakaoPcLogger.Interop;
using KakaoPcLogger.Models;

namespace KakaoPcLogger.Services
{
    public sealed class ChatWindowScanner
    {
        private readonly string _targetProcessName;
        private readonly string _chatListClass;

        public ChatWindowScanner(string targetProcessName, string chatListClass)
        {
            _targetProcessName = targetProcessName;
            _chatListClass = chatListClass;
        }

        //public IReadOnlyList<ChatEntry> Scan(bool autoInclude)
        //{
        //    var results = new List<ChatEntry>();

        //    NativeMethods.EnumWindows((hTop, _) =>
        //    {
        //        if (!NativeMethods.IsWindow(hTop))
        //            return true;

        //        if (!TryGetProcessInfo(hTop, out var pid, out var processName))
        //            return true;

        //        if (!string.Equals(processName, _targetProcessName, StringComparison.OrdinalIgnoreCase))
        //            return true;

        //        string parentTitle = GetWindowTextSafe(hTop);

        //        NativeMethods.EnumChildWindows(hTop, (hChild, __) =>
        //        {
        //            string className = GetClassNameSafe(hChild);
        //            if (string.Equals(className, _chatListClass, StringComparison.Ordinal))
        //            {
        //                results.Add(new ChatEntry
        //                {
        //                    Hwnd = hChild,
        //                    ParentHwnd = hTop,
        //                    Title = string.IsNullOrWhiteSpace(parentTitle) ? "(제목 없음/불명)" : parentTitle,
        //                    ClassName = className,
        //                    Pid = pid,
        //                    IsSelected = autoInclude
        //                });
        //            }
        //            return true;
        //        }, IntPtr.Zero);

        //        return true;
        //    }, IntPtr.Zero);

        //    return results;
        //}
        public IReadOnlyList<ChatEntry> Scan(bool autoInclude)
        {
            var results = new List<ChatEntry>();
            var seen = new HashSet<IntPtr>(); // 중복 방지(선택)

            NativeMethods.EnumWindows((hTop, _) =>
            {
                if (!TryGetProcessInfo(hTop, out var pid, out var processName))
                    return true;

                if (!string.Equals(processName, _targetProcessName, StringComparison.OrdinalIgnoreCase))
                    return true;

                // 부모(채팅방 창) 제목
                string parentTitle = GetWindowTextSafe(hTop).Trim();

                // ⬇️ 여기서 필터링: 제목이 없거나 정확히 "KakaoTalk"면 스킵
                if (string.IsNullOrWhiteSpace(parentTitle) ||
                    string.Equals(parentTitle, "KakaoTalk", StringComparison.OrdinalIgnoreCase))
                {
                    return true; // 이 최상위 창은 조사하지 않음
                }

                NativeMethods.EnumChildWindows(hTop, (hChild, __) =>
                {
                    string className = GetClassNameSafe(hChild);
                    if (string.Equals(className, _chatListClass, StringComparison.Ordinal))
                    {
                        if (seen.Add(hChild)) // (선택) 같은 컨트롤 중복 추가 방지
                        {
                            results.Add(new ChatEntry
                            {
                                Hwnd = hChild,
                                ParentHwnd = hTop,
                                Title = parentTitle,          // 더 이상 "(제목 없음/불명)" 사용 안 함
                                ClassName = className,
                                Pid = pid,
                                IsSelected = autoInclude
                            });
                        }
                    }
                    return true;
                }, IntPtr.Zero);

                return true;
            }, IntPtr.Zero);

            return results;
        }


        private static bool TryGetProcessInfo(IntPtr hWnd, out int pid, out string? processName)
        {
            pid = 0;
            processName = null;

            try
            {
                uint rawPid;
                NativeMethods.GetWindowThreadProcessId(hWnd, out rawPid);
                if (rawPid == 0)
                    return false;

                using var process = Process.GetProcessById((int)rawPid);
                pid = process.Id;
                processName = process.ProcessName;
                return true;
            }
            catch
            {
                pid = 0;
                processName = null;
                return false;
            }
        }

        private static string GetClassNameSafe(IntPtr hWnd)
        {
            var sb = new StringBuilder(256);
            NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static string GetWindowTextSafe(IntPtr hWnd)
        {
            var sb = new StringBuilder(512);
            NativeMethods.GetWindowText(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }
}
