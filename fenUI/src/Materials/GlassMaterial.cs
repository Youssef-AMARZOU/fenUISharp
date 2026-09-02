using FenUISharp.Logging;
using FenUISharp.Mathematics;
using FenUISharp.Objects;
using SkiaSharp;

namespace FenUISharp.Materials
{
    public class GlassMaterial : Material
    {
        public Func<SKColor> BaseColor
        {
            get => GetProp<Func<SKColor>>("BaseColor", () => SKColors.White);
            set => SetProp("BaseColor", value);
        }

        public Func<SKColor> Highlight
        {
            get => GetProp<Func<SKColor>>("Highlight", () => BaseColor().Multiply(1.5f).WithAlpha(150));
            set => SetProp("Highlight", value);
        }

        public Func<SKImage?> GrabPassFunction
        {
            get => GetProp<Func<SKImage?>>("GrabPassFunction", () => null);
            set => SetProp("GrabPassFunction", value);
        }

        public Func<float> BlurRadius
        {
            get => GetProp<Func<float>>("BlurRadius", () => 5);
            set => SetProp("BlurRadius", value);
        }

        public Func<float> Displacement
        {
            get => GetProp<Func<float>>("Displacement", () => 65);
            set => SetProp("Displacement", value);
        }

        public Func<int> Distance
        {
            get => GetProp<Func<int>>("Distance", () => 8);
            set => SetProp("Distance", value);
        }

        public Func<float> Brightness
        {
            get => GetProp<Func<float>>("Brightness", () => 1.3f);
            set => SetProp("Brightness", value);
        }

        private int DisplacementMapDownscale = 1;

        public GlassMaterial(Func<SKImage?> GrabPassFunction)
        {
            this.GrabPassFunction = GrabPassFunction;
        }

        // TODO: Causes crashes, engine execution errors
        protected override void Draw(SKCanvas targetCanvas, SKPath path, UIObject caller, SKPaint paint)
        {
            // Crisp solid style (macOS-like dark cards): skip the displacement glass
            // and blur entirely so surfaces stay sharp and readable.
            int unmodified = targetCanvas.Save();
            targetCanvas.ClipPath(path, antialias: true);

            paint.Shader = null;
            paint.ImageFilter = null;
            paint.Color = BaseColor().WithAlpha(242);
            targetCanvas.DrawPath(path, paint);

            // Subtle inner border for definition
            using (var strokePaint = new SKPaint
            {
                IsAntialias = true,
                IsStroke = true,
                StrokeWidth = 1.5f,
                Color = SKColors.White.WithAlpha(26)
            })
            {
                targetCanvas.DrawPath(path, strokePaint);
            }

            targetCanvas.RestoreToCount(unmodified);
            caller.Invalidate(UIObject.Invalidation.SurfaceDirty);
        }

        SKShader? CreateShader(SKShader displacementMap, SKShader grabPass, SKRect bounds, UIObject caller, SKColor baseColorMix, Vector2 off)
        {
            string sksl = @"
                uniform shader contentShader;
                uniform shader grabPass; // Content behind the glass

                uniform float3 iBaseMix;
                uniform float2 iResolution;
                uniform float2 iOff;
                uniform float2 iPathCorrection;
                uniform float iBright;

                uniform float iDisplacement;
                uniform float iDownscale;

                float rand(float x) {
                    return fract(sin(x * 12.9898) * 43758.5453123);
                }

                float4 colorLerp(float4 a, float4 b, float t) {
                    return mix(a, b, t);
                }

                float remapOffset(float x)
                {
                    // return -sin(x * (3.141/0.66)) * x;
                    return pow(x, 2);
                }

                half4 main(float2 fragCoord) {
                    float2 uv = (fragCoord - iOff);
                    float2 grabSampleUV = (fragCoord);

                    float4 offsetAmount = contentShader.eval((fragCoord + iPathCorrection) * iDownscale);
                    float4 simpleBack = float4(0, 0, 0, 0);
                    float4 backColor = float4(0, 0, 0, 0);

                    {
                        // float offsetSA = pow((1.0 - (offsetAmount.r)), 4) * iDisplacement;
                        float offsetSA = remapOffset(1.0 - (offsetAmount.r)) * iDisplacement;

                        float2 noiseVec = vec2(rand(dot(grabSampleUV, vec2(12.9898, 78.233))), rand(dot(grabSampleUV, vec2(39.3468, 11.135))));
                        float2 offset = float2(-offsetSA, offsetSA) + (noiseVec - 0.5) * 0.1; // subtle and centered noise

                        float2 displacedUV = uv + offset;
                        simpleBack += grabPass.eval(displacedUV);

                        // return half4(offsetSA / iDisplacement, 0, 0, 1);
                        // return half4(offset / iDisplacement, 0, 1);
                    }

                    if(offsetAmount.r > 0.99) 
                    {
                        backColor = simpleBack;
                    }
                    else
                    {
                        const float samples = 6;
                        for (float i = 0; i < samples; ++i) {
                            float v = (i - 2) * (12 / samples);
                            // float offsetSA = pow((1.0 - (offsetAmount.r)), 4) * iDisplacement + v;
                            float offsetSA = remapOffset(1.0 - (offsetAmount.r)) * iDisplacement;

                            float2 noiseVec = vec2(rand(dot(grabSampleUV, vec2(12.9898, 78.233))), rand(dot(grabSampleUV, vec2(39.3468, 11.135))));
                            float2 offset = float2(-offsetSA, offsetSA) + (noiseVec - 0.5) * 0.1; // subtle and centered noise

                            float2 displacedUV = uv + offset;
                            backColor += grabPass.eval(displacedUV);
                        }
                        backColor /= samples;
                        backColor = colorLerp(backColor, simpleBack, (offsetAmount.r));
                    }

                    float4 returnColor = (backColor * iBright + ((iBright - 1) / 2)) * float4(iBaseMix, 1);
                    returnColor.a = 1;

                    // return half4(fragCoord / iResolution + offset, 0, 1);
                    return returnColor;
                }
            ";

            SKRuntimeEffect effect = SKRuntimeEffect.CreateShader(sksl, out var err);
            if (effect == null) FLogger.Error($"Shader compilation failed: {err}");

            var uniforms = new SKRuntimeEffectUniforms(effect);
            uniforms["iBaseMix"] = new float[] { (float)baseColorMix.Red / 255f, (float)baseColorMix.Green / 255f, (float)baseColorMix.Blue / 255f };
            uniforms["iResolution"] = new float[] { bounds.Width, bounds.Height };
            uniforms["iOff"] = new float[] { (float)-caller.Padding.CachedValue, (float)-caller.Padding.CachedValue };
            uniforms["iPathCorrection"] = new float[] { (float)off.x, (float)off.y };
            uniforms["iDisplacement"] = (float)Displacement();
            uniforms["iBright"] = (float)Brightness();
            uniforms["iDownscale"] = (float)DisplacementMapDownscale;

            var children = new SKRuntimeEffectChildren(effect); // Content behind the glass
            children["contentShader"] = displacementMap;
            children["grabPass"] = grabPass;

            return effect?.ToShader(uniforms, children) ?? null;
        }
    }
}
