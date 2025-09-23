namespace KakaoPcLogger.Interop
{
    internal static class NativeConstants
    {
        internal const int WM_ACTIVATE = 0x0006;
        internal const int WA_ACTIVE = 1;
        internal const int WM_SETTEXT = 0x000C;
        internal const int WM_KEYDOWN = 0x0100;
        internal const int WM_KEYUP = 0x0101;
        internal const int WM_CHAR = 0x0102;
        internal const int WM_SYSKEYDOWN = 0x0104;
        internal const int WM_SYSKEYUP = 0x0105;
        internal const int WM_LBUTTONDOWN = 0x0201;
        internal const int WM_LBUTTONUP = 0x0202;

        internal const int VK_RETURN = 0x0D;
        internal const int VK_CONTROL = 0x11;
        internal const int VK_A = 0x41;
        internal const int VK_C = 0x43;
        internal const int VK_BACK = 0x08;   // Backspace key

        internal const int GWL_STYLE = -16;
        internal const int ES_MULTILINE = 0x0004;
        internal const int ES_READONLY = 0x0800;

        public const int EM_SETSEL = 0x00B1;  // Edit 계열: 캐럿/선택 설정
        public const uint EM_EXSETSEL = 0x0437;  // wParam=0, lParam=ref CHARRANGE

        // 셸 후킹을 위한 상수들
        internal const int HSHELL_WINDOWCREATED = 1;
        internal const int HSHELL_WINDOWDESTROYED = 2;
        internal const int HSHELL_REDRAW = 6;            // title/icon change
        internal const int HSHELL_HIGHBIT = 0x8000;
        internal const int HSHELL_FLASH = HSHELL_HIGHBIT | HSHELL_REDRAW; // 0x8006
        internal const uint GA_ROOT = 2;
        internal const uint GA_ROOTOWNER = 3;
    }
}
