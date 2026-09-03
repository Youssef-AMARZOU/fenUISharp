using System.Runtime.InteropServices;
using FenUISharp.Mathematics;
using FenUISharp.Native;
using Microsoft.Win32;
using SkiaSharp;

namespace FenUISharp
{
    public class FOverlayWindow : FTransparentWindow
    {
        private int _activeDisplay = 0;
        public int ActiveDisplayIndex { get => _activeDisplay; set { _activeDisplay = value; UpdateWindowMetrics(_activeDisplay); } }

        // Click-to-front state machine (single overlay instance assumed).
        // Pinned top = island in front. Unpinned = island behind other windows.
        internal static volatile bool IslandPinnedTop = true;
        internal static IntPtr LastForeground = IntPtr.Zero;

        public FOverlayWindow(
            string title, string className, int monitorIndex = 0) :
            base(title, className, new(0, 0), new(0, 0))
        {
            UpdateWindowMetrics(monitorIndex);

            // Exclude window from aero peek. Not needed with transparent overlays
            Properties.ExcludeFromAeroPeek = true;
            
            // Not needed in taskbar
            Properties.VisibleInTaskbar = false;

            // Force native topmost so the overlay always stays above other windows
            Properties.AlwaysOnTop = true;

            // Click-to-front enforcer thread: applies the desired z-order band a few
            // times per second. WM_WINDOWPOSCHANGING rewrites every pending position
            // (including the host app's own per-frame assertions) to match this state.
            var enforcer = new System.Threading.Thread(() =>
            {
                bool prevDown = false;
                while (true)
                {
                    try
                    {
                        var h = hWnd;
                        if (h != IntPtr.Zero)
                        {
                            bool inZone = false;
                            if (Win32APIs.GetCursorPos(out var cpt))
                            {
                                int screenW = Win32APIs.GetSystemMetrics(0);
                                inZone = cpt.y < 42 && Math.Abs(cpt.x - screenW / 2) < 220;
                            }

                            // Physical click edge: click on the island pins it to the
                            // front, clicking anywhere else sends it behind.
                            short key = Win32APIs.GetAsyncKeyState(0x01); // VK_LBUTTON
                            bool down = (key & 0x8000) != 0;
                            bool pressed = (key & 0x0001) != 0 || (down && !prevDown); // LSB = pressed since last call
                            prevDown = down;
                            if (pressed)
                            {
                                // Click decision is final for this tick
                                if (inZone && Win32APIs.WindowFromPoint(cpt) == h)
                                    IslandPinnedTop = true;
                                else
                                    IslandPinnedTop = false;
                            }
                            else if (inZone)
                            {
                                // Hovering the notch summons the island (when no click decision)
                                IslandPinnedTop = true;
                            }

                            // Foreground moved to another window -> island behind
                            IntPtr fg = Win32APIs.GetForegroundWindow();
                            if (fg != LastForeground)
                            {
                                if (fg != h) IslandPinnedTop = false;
                                LastForeground = fg;
                            }

                            Win32APIs.SetWindowPos(h, IslandPinnedTop ? new IntPtr(-1) : new IntPtr(-2), 0, 0, 0, 0,
                                (uint)(SetWindowPosFlags.SWP_NOMOVE | SetWindowPosFlags.SWP_NOSIZE | SetWindowPosFlags.SWP_NOACTIVATE));
                        }
                    }
                    catch { }
                    System.Threading.Thread.Sleep(120);
                }
            })
            { IsBackground = true };
            enforcer.Start();
        }

        public void UpdateWindowMetrics(int activeMonitorDisplay = 0)
        {
            int x, y, width, height;

            // if (activeMonitorDisplay == 0)
            // {
            //     // Use primary monitor metrics from system metrics
            //     width = Win32APIs.GetSystemMetrics(0);  // SM_CXSCREEN
            //     height = Win32APIs.GetSystemMetrics(1); // SM_CYSCREEN
            //     x = 0;
            //     y = 0;
            // }
            // else
            {
                // Otherwise get rect of monitor from the index
                var monitorRect = Win32APIs.GetMonitorRect(activeMonitorDisplay);

                // Extract values
                x = monitorRect.left;
                y = monitorRect.top;
                width = monitorRect.right - monitorRect.left;
                height = monitorRect.bottom - monitorRect.top;
            }

            // Set window position and size (HWND_TOPMOST keeps the overlay above everything)
            Win32APIs.SetWindowPos(hWnd, new IntPtr(-1) /* HWND_TOPMOST */, x, y, width, height, (uint)SetWindowPosFlags.SWP_NOACTIVATE);

            // Trigger buffer invalidation
            FullRedraw();

            // Trigger wndarea rebuild
            Shape.RebuildWindowArea();
        }
    }
}