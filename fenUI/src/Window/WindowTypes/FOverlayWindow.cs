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
            //
            // Summon gesture: push the mouse against the very top edge of the screen
            // ("the roof"). A plain hover near the top does NOT summon.
            var enforcer = new System.Threading.Thread(() =>
            {
                bool prevDown = false;
                DateTime lastNearIsland = DateTime.Now;
                while (true)
                {
                    try
                    {
                        var h = hWnd;
                        if (h != IntPtr.Zero)
                        {
                            var cpt = default(Native.POINT);
                            bool atRoof = false;
                            bool nearIsland = false;
                            if (Win32APIs.GetCursorPos(out cpt))
                            {
                                int screenW = Win32APIs.GetSystemMetrics(0);
                                // Summon gesture: push the mouse against the very top edge
                                // at the MIDDLE of the screen only (the notch area).
                                atRoof = cpt.y <= 2 && Math.Abs(cpt.x - screenW / 2) < 120;
                                // "Near the island" = the notch/pill zone
                                nearIsland = cpt.y < 42 && Math.Abs(cpt.x - screenW / 2) < 300;
                            }

                            // Auto-hide: pinned island goes behind all windows once the
                            // mouse has stayed away from it for 5 seconds.
                            if (nearIsland)
                                lastNearIsland = DateTime.Now;
                            else if (IslandPinnedTop && (DateTime.Now - lastNearIsland).TotalSeconds >= 5)
                                IslandPinnedTop = false;

                            // Physical click edge: clicking the island's pill area pins it
                            // to the front, clicking anywhere else sends it behind.
                            short key = Win32APIs.GetAsyncKeyState(0x01); // VK_LBUTTON
                            bool down = (key & 0x8000) != 0;
                            bool pressed = (key & 0x0001) != 0 || (down && !prevDown); // LSB = pressed since last call
                            prevDown = down;
                            if (pressed)
                            {
                                // Only a click inside the notch/pill zone that hits one of
                                // OUR windows (overlay or its popovers) pins the island.
                                int screenW = Win32APIs.GetSystemMetrics(0);
                                bool inPillZone = cpt.y < 42 && Math.Abs(cpt.x - screenW / 2) < 300;
                                uint hitPid = 0;
                                Win32APIs.GetWindowThreadProcessId(Win32APIs.WindowFromPoint(cpt), out hitPid);
                                bool onIslandPill = inPillZone && hitPid == (uint)System.Environment.ProcessId;
                                IslandPinnedTop = onIslandPill;
                            }
                            else if (atRoof && !down)
                            {
                                // Mouse pressed against the roof summons the island
                                // (ignored while dragging so window-maximize drags don't trigger it)
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