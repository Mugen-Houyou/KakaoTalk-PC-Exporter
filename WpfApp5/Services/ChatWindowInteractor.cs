using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Automation;
using KakaoPcLogger.Interop;
using KakaoPcLogger.Models;

namespace KakaoPcLogger.Services
{
    public sealed class ChatWindowInteractor
    {
        public bool Validate(ChatEntry entry, out string? warning)
        {
            if (!NativeMethods.IsWindow(entry.ParentHwnd) || !NativeMethods.IsWindow(entry.Hwnd))
            {
                warning = $"[경고] 무효 핸들: {entry.Title} {entry.HwndHex}";
                return false;
            }

            warning = null;
            return true;
        }

        public bool TryReadAllText(ChatEntry entry, out string? text, out string? warning)
        {
            text = null;
            warning = null;

            if (!NativeMethods.IsWindow(entry.Hwnd))
            {
                warning = $"[경고] 무효 핸들: {entry.Title} {entry.HwndHex}";
                return false;
            }

            try
            {
                var element = AutomationElement.FromHandle(entry.Hwnd);
                if (element is null)
                {
                    warning = $"[경고] UI Automation 요소 생성 실패: {entry.Title}";
                    return false;
                }

                text = ExtractText(element);
                text ??= string.Empty;
                return true;
            }
            catch (ElementNotAvailableException)
            {
                warning = $"[경고] UI 요소가 닫혔습니다: {entry.Title}";
                return false;
            }
            catch (InvalidOperationException ex)
            {
                warning = $"[경고] UI Automation 오류: {ex.Message}";
                return false;
            }
            catch (Exception ex)
            {
                warning = $"[경고] UI Automation 예기치 못한 오류: {ex.Message}";
                return false;
            }
        }

        private static string ExtractText(AutomationElement element)
        {
            if (TryGetPatternText(element, out var directText))
            {
                return NormalizeText(directText);
            }

            var sb = new StringBuilder();
            string? lastSegment = null;

            AppendSegment(sb, TryExtractElementText(element), ref lastSegment);
            CollectDescendantText(element, sb, ref lastSegment);

            return sb.ToString();
        }

        private static void CollectDescendantText(AutomationElement root, StringBuilder sb, ref string? lastSegment)
        {
            var queue = new Queue<AutomationElement>();

            try
            {
                var initial = root.FindAll(TreeScope.Children, Condition.TrueCondition);
                for (int i = 0; i < initial.Count; i++)
                {
                    queue.Enqueue(initial[i]);
                }
            }
            catch (ElementNotAvailableException)
            {
                return;
            }

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();

                string? text = null;
                try
                {
                    text = TryExtractElementText(current);
                }
                catch (ElementNotAvailableException)
                {
                    continue;
                }

                AppendSegment(sb, text, ref lastSegment);

                try
                {
                    var children = current.FindAll(TreeScope.Children, Condition.TrueCondition);
                    for (int i = 0; i < children.Count; i++)
                    {
                        queue.Enqueue(children[i]);
                    }
                }
                catch (ElementNotAvailableException)
                {
                }
            }
        }

        private static bool TryGetPatternText(AutomationElement element, out string? text)
        {
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObj) && textPatternObj is TextPattern textPattern)
            {
                text = textPattern.DocumentRange.GetText(-1);
                if (!string.IsNullOrEmpty(text))
                {
                    return true;
                }
            }

            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObj) && valuePatternObj is ValuePattern valuePattern)
            {
                text = valuePattern.Current.Value;
                if (!string.IsNullOrEmpty(text))
                {
                    return true;
                }
            }

            text = null;
            return false;
        }

        private static string? TryExtractElementText(AutomationElement element)
        {
            try
            {
                if (element.TryGetCurrentPattern(TextPattern.Pattern, out var textPatternObj) && textPatternObj is TextPattern textPattern)
                {
                    string text = textPattern.DocumentRange.GetText(-1);
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }

                if (element.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePatternObj) && valuePatternObj is ValuePattern valuePattern)
                {
                    string text = valuePattern.Current.Value;
                    if (!string.IsNullOrEmpty(text))
                    {
                        return text;
                    }
                }

                if (element.TryGetCurrentPattern(LegacyIAccessiblePattern.Pattern, out var legacyPatternObj) && legacyPatternObj is LegacyIAccessiblePattern legacy)
                {
                    string? value = legacy.Current.Value;
                    if (!string.IsNullOrEmpty(value))
                    {
                        return value;
                    }

                    string? name = legacy.Current.Name;
                    if (!string.IsNullOrEmpty(name))
                    {
                        return name;
                    }
                }

                var controlType = element.Current.ControlType;
                string nameProperty = element.Current.Name;

                if (!string.IsNullOrEmpty(nameProperty))
                {
                    if (controlType == ControlType.Text ||
                        controlType == ControlType.Document ||
                        controlType == ControlType.ListItem ||
                        controlType == ControlType.Edit)
                    {
                        return nameProperty;
                    }
                }
            }
            catch (ElementNotAvailableException)
            {
                return null;
            }

            return null;
        }

        private static void AppendSegment(StringBuilder sb, string? rawText, ref string? lastSegment)
        {
            if (string.IsNullOrWhiteSpace(rawText))
            {
                return;
            }

            string normalized = NormalizeText(rawText);

            if (string.IsNullOrEmpty(normalized))
            {
                return;
            }

            if (string.Equals(normalized, lastSegment, StringComparison.Ordinal))
            {
                return;
            }

            if (sb.Length > 0)
            {
                sb.AppendLine();
            }

            sb.Append(normalized);
            lastSegment = normalized;
        }

        private static string NormalizeText(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return string.Empty;
            }

            string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
            normalized = normalized.Trim('\n');
            return normalized.Replace("\n", Environment.NewLine);
        }
    }
}
