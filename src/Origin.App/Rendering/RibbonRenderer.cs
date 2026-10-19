using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using SharpDX.Direct3D9;
using SharpDX.Mathematics.Interop;

namespace Origin.Rendering
{
    /// <summary>
    /// Draws Origin's animated background — a deep blue field crossed by slowly twisting,
    /// translucent light ribbons in the spirit of Windows Media Center — with Direct3D 9Ex.
    /// It renders into an off-screen surface that WPF composites through a D3DImage. Only the
    /// fixed-function pipeline is used, so no shader compiler is needed and any Windows 7
    /// WDDM driver can run it.
    /// </summary>
    internal sealed class RibbonRenderer : IDisposable
    {
        private const int Segments = 120;

        private static readonly Ribbon[] Ribbons =
        {
            // baseY, amplitude, thickness, freq1, freq2, speed1, speed2, phase, twistFreq, twistSpeed, rgb, alpha
            new Ribbon(0.64f, 0.10f, 0.120f, 0.55f, 1.30f, 0.110f, 0.070f, 0.0f, 0.70f, 0.090f, 0x3FA8FF, 0.26f),
            new Ribbon(0.68f, 0.08f, 0.075f, 0.80f, 1.70f, 0.090f, 0.130f, 1.9f, 1.10f, 0.120f, 0x5CE1E6, 0.22f),
            new Ribbon(0.60f, 0.12f, 0.050f, 0.45f, 2.10f, 0.060f, 0.100f, 3.4f, 0.90f, 0.070f, 0x9FD8FF, 0.20f),
            new Ribbon(0.73f, 0.06f, 0.160f, 0.35f, 0.90f, 0.050f, 0.040f, 5.1f, 0.50f, 0.050f, 0x1E6FD8, 0.30f),
            new Ribbon(0.55f, 0.09f, 0.030f, 1.10f, 2.60f, 0.140f, 0.080f, 2.2f, 1.60f, 0.150f, 0xFFFFFF, 0.16f),
            new Ribbon(0.77f, 0.05f, 0.045f, 0.90f, 1.90f, 0.100f, 0.160f, 4.0f, 1.30f, 0.100f, 0x7EF0D0, 0.15f),
            new Ribbon(0.50f, 0.07f, 0.020f, 1.40f, 3.10f, 0.170f, 0.120f, 0.8f, 2.00f, 0.180f, 0xBFE8FF, 0.12f),
        };

        private readonly Vertex[] _strip = new Vertex[(Segments + 1) * 2];
        private readonly Vertex[] _quad = new Vertex[4];
        private readonly Vertex[] _fan = new Vertex[34];
        private readonly float[] _center = new float[Segments + 1];
        private readonly float[] _half = new float[Segments + 1];
        private readonly float[] _glow = new float[Segments + 1];

        private HwndSource _deviceWindow;
        private Direct3DEx _direct3D;
        private DeviceEx _device;
        private Surface _target;
        private int _width;
        private int _height;

        public IntPtr SurfacePointer => _target?.NativePointer ?? IntPtr.Zero;

        public int Width => _width;

        public int Height => _height;

        public void Initialize()
        {
            // Device creation needs a window; it is never shown and nothing is presented to it.
            _deviceWindow = new HwndSource(0, 0, 0, 0, 0, "Origin Direct3D", IntPtr.Zero);
            _direct3D = new Direct3DEx();

            var caps = _direct3D.GetDeviceCaps(0, DeviceType.Hardware);
            var flags = (caps.DeviceCaps & DeviceCaps.HWTransformAndLight) != 0
                ? CreateFlags.HardwareVertexProcessing
                : CreateFlags.SoftwareVertexProcessing;

            var parameters = new PresentParameters
            {
                Windowed = true,
                SwapEffect = SwapEffect.Discard,
                DeviceWindowHandle = _deviceWindow.Handle,
                PresentationInterval = PresentInterval.Immediate,
                BackBufferWidth = 1,
                BackBufferHeight = 1,
                BackBufferFormat = Format.Unknown,
            };

            _device = new DeviceEx(_direct3D, 0, DeviceType.Hardware, _deviceWindow.Handle,
                flags | CreateFlags.Multithreaded | CreateFlags.FpuPreserve, parameters);
        }

        public void SetSize(int width, int height)
        {
            if (width == _width && height == _height && _target != null)
            {
                return;
            }

            _target?.Dispose();
            _target = Surface.CreateRenderTarget(_device, width, height, Format.A8R8G8B8, MultisampleType.None, 0, false);
            _width = width;
            _height = height;
        }

        /// <summary>True if the GPU is still usable; false means the device must be recreated.</summary>
        public bool CheckDevice()
        {
            // The helper window is never shown, so "occluded" and similar presentation states
            // are normal here; only a lost, hung or removed device needs rebuilding.
            var state = _device.CheckDeviceState(_deviceWindow.Handle);
            return state != DeviceState.DeviceLost && state != DeviceState.DeviceHung && state != DeviceState.DeviceRemoved;
        }

        public void Render(double seconds)
        {
            var t = (float)seconds;

            _device.SetRenderTarget(0, _target);
            _device.Clear(ClearFlags.Target, new RawColorBGRA(0x24, 0x0C, 0x02, 0xFF), 1f, 0);
            _device.BeginScene();

            _device.SetRenderState(RenderState.Lighting, false);
            _device.SetRenderState(RenderState.ZEnable, false);
            _device.SetRenderState(RenderState.CullMode, Cull.None);
            _device.SetRenderState(RenderState.ShadeMode, ShadeMode.Gouraud);
            _device.SetTexture(0, null);
            _device.SetTextureStageState(0, TextureStage.ColorOperation, TextureOperation.SelectArg1);
            _device.SetTextureStageState(0, TextureStage.ColorArg1, TextureArgument.Diffuse);
            _device.SetTextureStageState(0, TextureStage.AlphaOperation, TextureOperation.SelectArg1);
            _device.SetTextureStageState(0, TextureStage.AlphaArg1, TextureArgument.Diffuse);
            _device.VertexFormat = VertexFormat.PositionRhw | VertexFormat.Diffuse;

            DrawBackdrop(t);

            // Ribbons add light: overlapping ribbons brighten, like the Media Center background.
            _device.SetRenderState(RenderState.AlphaBlendEnable, true);
            _device.SetRenderState(RenderState.SourceBlend, Blend.SourceAlpha);
            _device.SetRenderState(RenderState.DestinationBlend, Blend.One);

            DrawGlow(t);
            foreach (var ribbon in Ribbons)
            {
                DrawRibbon(ribbon, t);
            }

            _device.EndScene();
        }

        public void Dispose()
        {
            _target?.Dispose();
            _target = null;
            _device?.Dispose();
            _device = null;
            _direct3D?.Dispose();
            _direct3D = null;
            _deviceWindow?.Dispose();
            _deviceWindow = null;
        }

        private void DrawBackdrop(float t)
        {
            _device.SetRenderState(RenderState.AlphaBlendEnable, false);

            // A slow "breath" in the top of the gradient keeps the field from looking static.
            var lift = 0.5f + 0.5f * (float)Math.Sin(t * 0.08);
            var top = Lerp(0x0A3170, 0x0D3D86, lift);

            _quad[0] = new Vertex(0, 0, Argb(255, top));
            _quad[1] = new Vertex(_width, 0, Argb(255, 0x072963));
            _quad[2] = new Vertex(0, _height, Argb(255, 0x03112C));
            _quad[3] = new Vertex(_width, _height, Argb(255, 0x020A1E));
            _device.DrawUserPrimitives(PrimitiveType.TriangleStrip, 2, _quad);
        }

        /// <summary>A soft pool of light drifting behind the ribbons.</summary>
        private void DrawGlow(float t)
        {
            var cx = _width * (0.32f + 0.10f * (float)Math.Sin(t * 0.045));
            var cy = _height * (0.30f + 0.06f * (float)Math.Cos(t * 0.037));
            var rx = _width * 0.75f;
            var ry = _height * 0.70f;

            _fan[0] = new Vertex(cx, cy, Argb(70, 0x3C8DE0));
            for (var i = 1; i < _fan.Length; i++)
            {
                var angle = (i - 1) * Math.PI * 2 / (_fan.Length - 2);
                _fan[i] = new Vertex(cx + rx * (float)Math.Cos(angle), cy + ry * (float)Math.Sin(angle), Argb(0, 0x3C8DE0));
            }

            _device.DrawUserPrimitives(PrimitiveType.TriangleFan, _fan.Length - 2, _fan);
        }

        private void DrawRibbon(Ribbon r, float t)
        {
            const double Tau = Math.PI * 2;
            var breathe = 0.85f + 0.15f * (float)Math.Sin(t * 0.3 + r.Phase);

            for (var i = 0; i <= Segments; i++)
            {
                var x = -0.05f + 1.10f * i / Segments;

                // Two travelling sine waves, a gentle upward tilt to the right and a slow vertical drift.
                var y = r.BaseY
                        + r.Amplitude * (0.65f * (float)Math.Sin(Tau * r.Freq1 * x + r.Speed1 * t + r.Phase)
                                         + 0.35f * (float)Math.Sin(Tau * r.Freq2 * x - r.Speed2 * t + 1.7f * r.Phase))
                        - 0.12f * (x - 0.5f)
                        + 0.03f * (float)Math.Sin(0.1 * t + r.Phase);

                // The ribbon "twists": its width pinches towards zero and opens again along its length.
                var twist = Math.Abs((float)Math.Sin(Math.PI * r.TwistFreq * x * 2 + r.TwistSpeed * t + r.Phase));
                var half = r.Thickness * (0.12f + 0.88f * twist) * 0.5f;

                // Fade in from the left edge and out towards the right.
                var fade = SmoothStep(-0.05f, 0.30f, x) * (1f - 0.55f * SmoothStep(0.65f, 1.05f, x));

                _center[i] = y * _height;
                _half[i] = half * _height;
                _glow[i] = Clamp01(r.Alpha * breathe * fade * (0.75f + 0.5f * (1f - twist)));
            }

            // Soft body: transparent edges, brightest along the centre line.
            BuildStrip(r.Color, 1f, upper: true);
            _device.DrawUserPrimitives(PrimitiveType.TriangleStrip, Segments * 2, _strip);
            BuildStrip(r.Color, 1f, upper: false);
            _device.DrawUserPrimitives(PrimitiveType.TriangleStrip, Segments * 2, _strip);

            // A thin, bright core gives the glassy highlight.
            var core = Lerp(r.Color, 0xFFFFFF, 0.6f);
            BuildStrip(core, 0.08f, upper: true, intensity: 1.8f);
            _device.DrawUserPrimitives(PrimitiveType.TriangleStrip, Segments * 2, _strip);
            BuildStrip(core, 0.08f, upper: false, intensity: 1.8f);
            _device.DrawUserPrimitives(PrimitiveType.TriangleStrip, Segments * 2, _strip);
        }

        private void BuildStrip(int rgb, float widthScale, bool upper, float intensity = 1f)
        {
            for (var i = 0; i <= Segments; i++)
            {
                var x = _width * (-0.05f + 1.10f * i / Segments);
                var edge = _center[i] + (upper ? -1 : 1) * _half[i] * widthScale;
                var alpha = (int)(255 * Clamp01(_glow[i] * intensity));

                _strip[i * 2] = new Vertex(x, edge, Argb(0, rgb));
                _strip[i * 2 + 1] = new Vertex(x, _center[i], Argb(alpha, rgb));
            }
        }

        private static int Argb(int alpha, int rgb) => (alpha << 24) | (rgb & 0xFFFFFF);

        private static int Lerp(int from, int to, float amount)
        {
            int Channel(int shift) => (int)(((from >> shift) & 0xFF) + (((to >> shift) & 0xFF) - ((from >> shift) & 0xFF)) * amount) << shift;
            return Channel(16) | Channel(8) | Channel(0);
        }

        private static float SmoothStep(float edge0, float edge1, float x)
        {
            var v = Clamp01((x - edge0) / (edge1 - edge0));
            return v * v * (3 - 2 * v);
        }

        private static float Clamp01(float v) => v < 0 ? 0 : v > 1 ? 1 : v;

        [StructLayout(LayoutKind.Sequential)]
        private struct Vertex
        {
            public float X;
            public float Y;
            public float Z;
            public float Rhw;
            public int Color;

            public Vertex(float x, float y, int color)
            {
                X = x;
                Y = y;
                Z = 0.5f;
                Rhw = 1f;
                Color = color;
            }
        }

        private sealed class Ribbon
        {
            public Ribbon(float baseY, float amplitude, float thickness, float freq1, float freq2, float speed1, float speed2,
                float phase, float twistFreq, float twistSpeed, int color, float alpha)
            {
                BaseY = baseY;
                Amplitude = amplitude;
                Thickness = thickness;
                Freq1 = freq1;
                Freq2 = freq2;
                Speed1 = speed1;
                Speed2 = speed2;
                Phase = phase;
                TwistFreq = twistFreq;
                TwistSpeed = twistSpeed;
                Color = color;
                Alpha = alpha;
            }

            public float BaseY { get; }

            public float Amplitude { get; }

            public float Thickness { get; }

            public float Freq1 { get; }

            public float Freq2 { get; }

            public float Speed1 { get; }

            public float Speed2 { get; }

            public float Phase { get; }

            public float TwistFreq { get; }

            public float TwistSpeed { get; }

            public int Color { get; }

            public float Alpha { get; }
        }
    }
}
