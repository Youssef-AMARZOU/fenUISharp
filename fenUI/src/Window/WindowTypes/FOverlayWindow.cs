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

        // Click-to-front state machine v7 (single overlay instance assumed).
        // Pinned top = island in front. Unpinned = island behind other windows.
        internal static volatile bool IslandPinnedTop = true;
        internal static IntPtr LastForeground = IntPtr.Zero;
        // True while an expanded panel (music player, calendar, ...) is open:
        // the whole overlay then accepts clicks, otherwise only the pill strip.
        internal static volatile bool IslandExpanded = false;

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
                int roofTicks = 0;
                int tick = 0;
                try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dw_enforcer_alive.txt"), "started " + DateTime.Now.ToString("HH:mm:ss.fff")); } catch { }
                while (true)
                {
                    try
                    {
                        var h = hWnd;
                        tick++;
                        if (tick % 25 == 1)
                        {
                            try { System.IO.File.WriteAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "dw_enforcer_alive.txt"), $"tick {tick} h={h} pinned={IslandPinnedTop} {DateTime.Now.ToString("HH:mm:ss.fff")}"); } catch { }
                        }
                        if (h != IntPtr.Zero)
                        {
                            var cpt = default(Native.POINT);
                            bool atRoof = false;
                            bool nearIsland = false;
                            if (Win32APIs.GetCursorPos(out cpt))
                            {
                                int screenW = Win32APIs.GetSystemMetrics(0);
                                // Summon gesture: push the mouse against the very top edge
                                // at the MIDDLE of the screen only (the notch area), and
                                // HOLD it there briefly so passing moves don't summon.
                                atRoof = cpt.y <= 2 && Math.Abs(cpt.x - screenW / 2) < 120;
                                // "Near the island" = the notch/pill zone
                                nearIsland = cpt.y < 42 && Math.Abs(cpt.x - screenW / 2) < 300;
                            }

                            // Auto-hide: pinned island goes behind all windows once the
                            // mouse has stayed away from it for 5 seconds.
                            if (nearIsland)
                                lastNearIsland = DateTime.Now;
                            else if (IslandPinnedTop && (DateTime.Now - lastNearIsland).TotalSeconds >= 5)
                            {
                                IslandPinnedTop = false;
                                IslandExpanded = false;
                            }

                            // Physical click edge: clicking the island's pill area pins it
                            // to the front (and expands it), clicking anywhere else sends
                            // it behind. The click itself passes through the transparent
                            // parts and reaches the app behind.
                            short key = Win32APIs.GetAsyncKeyState(0x01); // VK_LBUTTON
                            bool down = (key & 0x8000) != 0;
                            bool pressed = (key & 0x0001) != 0 || (down && !prevDown); // LSB = pressed since last call
                            prevDown = down;
                            if (pressed)
                            {
                                // Click landed on one of OUR windows (pill, expanded
                                // panel, popover) -> island in front. Landed on any other
                                // app (the transparent parts let these clicks through)
                                // -> island behind.
                                uint hitPid = 0;
                                Win32APIs.GetWindowThreadProcessId(Win32APIs.WindowFromPoint(cpt), out hitPid);
                                bool onIsland = hitPid == (uint)System.Environment.ProcessId;
                                IslandPinnedTop = onIsland;
                                IslandExpanded = onIsland;
                                if (!onIsland) lastNearIsland = DateTime.Now;
                            }
                            else if (atRoof && !down)
                            {
                                // Mouse held against the roof summons the island
                                // (ignored while dragging so window-maximize drags don't trigger it)
                                roofTicks++;
                                if (roofTicks >= 3)
                                    IslandPinnedTop = true;
                            }
                            else
                                roofTicks = 0;

                            // Foreground moved to another window -> island behind
                            IntPtr fg = Win32APIs.GetForegroundWindow();
                            if (fg != LastForeground)
                            {
                                if (fg != h)
                                {
                                    IslandPinnedTop = false;
                                    IslandExpanded = false;
                                }
                                LastForeground = fg;
                            }

                            if (!IslandPinnedTop)
                                IslandExpanded = false;

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

        // Click-through everywhere except the pill strip, so the invisible
        // overlay never blocks clicks on the apps behind it. While an expanded
        // panel is open the whole surface stays interactive.
        internal override IntPtr WindowHitTest(IntPtr wParam, IntPtr lParam)
        {
            if (IslandExpanded) return IntPtr.Zero;

            long l = lParam.ToInt64();
            int x = (short)(l & 0xFFFF);
            int y = (short)((l >> 16) & 0xFFFF);
            int screenW = Win32APIs.GetSystemMetrics(0);
            if (y < 50 && Math.Abs(x - screenW / 2) < 180)
                return IntPtr.Zero;

            return new IntPtr(-1); // HTTRANSPARENT - let the click pass through
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
