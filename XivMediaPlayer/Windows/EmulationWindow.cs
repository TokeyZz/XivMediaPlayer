using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using ImGuiNET;
using System;
using System.Numerics;
using XivMediaPlayer.Networking;

namespace XivMediaPlayer.Windows
{
    public class EmulationWindow : Window
    {
        private readonly VideoWindow _videoWindow;
        public EmulationClient? EmulationClient { get; set; }

        public EmulationWindow(VideoWindow videoWindow)
            : base("Emulation Screen (Remote Desktop)", ImGuiWindowFlags.NoScrollbar)
        {
            _videoWindow = videoWindow;
            Size = new Vector2(800, 450);
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        public override void Draw()
        {
            if (EmulationClient == null)
            {
                ImGui.Text("Not connected to an emulation server.");
                return;
            }

            var tex = _videoWindow.GetCurrentTextureWrap();
            if (tex == null)
            {
                ImGui.Text("Waiting for video feed...");
                return;
            }

            // Draw video taking up available space
            var avail = ImGui.GetContentRegionAvail();
            
            // Maintain 16:9 aspect ratio
            float aspect = 16f / 9f;
            float w = avail.X;
            float h = w / aspect;
            if (h > avail.Y)
            {
                h = avail.Y;
                w = h * aspect;
            }

            // Center the image
            ImGui.SetCursorPos(new Vector2(ImGui.GetCursorPosX() + (avail.X - w) / 2, ImGui.GetCursorPosY() + (avail.Y - h) / 2));

            Vector2 p0 = ImGui.GetCursorScreenPos();
            ImGui.Image(tex.Handle, new Vector2(w, h));
            Vector2 p1 = new Vector2(p0.X + w, p0.Y + h);

            if (ImGui.IsItemHovered())
            {
                Vector2 mouse = ImGui.GetMousePos();
                
                // Calculate 0-1 normalized coordinates
                float normX = Math.Clamp((mouse.X - p0.X) / w, 0f, 1f);
                float normY = Math.Clamp((mouse.Y - p0.Y) / h, 0f, 1f);

                // Convert to 0-255 for payload
                byte xByte = (byte)(normX * 255f);
                byte yByte = (byte)(normY * 255f);

                bool lmb = ImGui.IsMouseDown(ImGuiMouseButton.Left);
                bool rmb = ImGui.IsMouseDown(ImGuiMouseButton.Right);

                EmulationClient.SendMouseState(xByte, yByte, lmb, rmb);
            }
        }
    }
}
