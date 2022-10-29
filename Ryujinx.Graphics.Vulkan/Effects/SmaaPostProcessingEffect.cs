using Ryujinx.Graphics.GAL;
using Ryujinx.Graphics.Shader;
using Ryujinx.Graphics.Shader.Translation;
using Silk.NET.Vulkan;
using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Format = Ryujinx.Graphics.GAL.Format;

namespace Ryujinx.Graphics.Vulkan.Effects
{
    internal partial class SmaaPostProcessingEffect : IPostProcessingEffect
    {
        private readonly VulkanRenderer _renderer;
        private ISampler _samplerLinear;
        private ShaderCollection _edgeProgram;
        private ShaderCollection _blendProgram;
        private ShaderCollection _neighbourProgram;

        private PipelineHelperShader _edgePipeline;
        private PipelineHelperShader _blendPipeline;
        private PipelineHelperShader _neighbourPipleline;
        private TextureView _outputTexture;
        private TextureView _edgeOutputTexture;
        private TextureView _blendOutputTexture;
        private TextureView _areaTexture;
        private TextureView _searchTexture;
        private Device _device;
        private bool _recreatePipelines;
        private int _quality;

        public SmaaPostProcessingEffect(VulkanRenderer renderer, Device device, int quality)
        {
            _device = device;
            _renderer = renderer;
            _quality = quality;
            Initialize();
        }

        public int Quality
        {
            get => _quality; 
            set
            {
                _quality = value;

                _recreatePipelines = true;
            }
        }

        public void Dispose()
        {
            DeletePipelines();
            _samplerLinear?.Dispose();
            _outputTexture?.Dispose();
            _edgeOutputTexture?.Dispose();
            _blendOutputTexture?.Dispose();
            _areaTexture?.Dispose();
            _searchTexture?.Dispose();
        }

        private unsafe void RecreateShaders(int width, int height)
        {
            _recreatePipelines = false;

            DeletePipelines();
            _edgePipeline = new PipelineHelperShader(_renderer, _device);
            _blendPipeline = new PipelineHelperShader(_renderer, _device);
            _neighbourPipleline = new PipelineHelperShader(_renderer, _device);

            _edgePipeline.Initialize();
            _blendPipeline.Initialize();
            _neighbourPipleline.Initialize();

            var edgeBindings = new ShaderBindings(
                new[] { 2 },
                Array.Empty<int>(),
                new[] { 1 },
                new[] { 0 });

            var blendBindings = new ShaderBindings(
                new[] { 2 },
                Array.Empty<int>(),
                new[] { 1, 3, 4 },
                new[] { 0 });

            var neighbourBindings = new ShaderBindings(
                new[] { 2 },
                Array.Empty<int>(),
                new[] { 1, 3 },
                new[] { 0 });

            _samplerLinear = _renderer.CreateSampler(GAL.SamplerCreateInfo.Create(MinFilter.Linear, MagFilter.Linear));

            var constantSize = sizeof(SmaaConstants);
            var constants = new SmaaConstants()
            {
                Width = width,
                Height = height,
                QualityLow = Quality == 0 ? 1 : 0,
                QualityMedium = Quality == 1 ? 1 : 0,
                QualityHigh = Quality == 2 ? 1 : 0,
                QualityUltra = Quality == 3 ? 1 : 0,
            };

            var data = new NativeArray<byte>(constantSize);
            Marshal.StructureToPtr(constants, (IntPtr)data.Pointer, false);

            var specializationInfo = new ShaderSpecializationInfo(
                new[]
                {
                    new SpecializationEntry(0, 0, sizeof(int)),
                    new SpecializationEntry(1, (uint)Unsafe.ByteOffset(ref Unsafe.As<SmaaConstants, int>(ref constants), ref constants.QualityMedium), sizeof(int)),
                    new SpecializationEntry(2, (uint)Unsafe.ByteOffset(ref Unsafe.As<SmaaConstants, int>(ref constants), ref constants.QualityHigh), sizeof(int)),
                    new SpecializationEntry(3, (uint)Unsafe.ByteOffset(ref Unsafe.As<SmaaConstants, int>(ref constants), ref constants.QualityUltra), sizeof(int)),
                    new SpecializationEntry(4, (uint)Unsafe.ByteOffset(ref Unsafe.As<SmaaConstants, float>(ref constants), ref constants.Width), sizeof(float)),
                    new SpecializationEntry(5, (uint)Unsafe.ByteOffset(ref Unsafe.As<SmaaConstants, float>(ref constants), ref constants.Height), sizeof(float)),
                },
                data);

            _edgeProgram = _renderer.CreateProgramWithMinimalLayout(new[]
            {
                new ShaderSource(this.SmaaEdgeShader, edgeBindings, ShaderStage.Compute, TargetLanguage.Spirv)
            }, new[] { specializationInfo });

            _blendProgram = _renderer.CreateProgramWithMinimalLayout(new[]
            {
                new ShaderSource(this.SmaaBlendShader, blendBindings, ShaderStage.Compute, TargetLanguage.Spirv)
            }, new[] { specializationInfo });

            _neighbourProgram = _renderer.CreateProgramWithMinimalLayout(new[]
            {
                new ShaderSource(this.SmaaNeighbourShader, neighbourBindings, ShaderStage.Compute, TargetLanguage.Spirv)
            }, new[] { specializationInfo });
        }

        public void DeletePipelines()
        {
            _edgePipeline?.Dispose();
            _blendPipeline?.Dispose();
            _neighbourPipleline?.Dispose();
            _edgeProgram?.Dispose();
            _blendProgram?.Dispose();
            _neighbourProgram?.Dispose();
        }

        public void Initialize()
        {
            var areaInfo = new TextureCreateInfo(AreaWidth,
                AreaHeight,
                1,
                1,
                1,
                1,
                1,
                1,
                Format.R8G8Unorm,
                DepthStencilMode.Depth,
                Target.Texture2D,
                SwizzleComponent.Red,
                SwizzleComponent.Green,
                SwizzleComponent.Blue,
                SwizzleComponent.Alpha);

            var searchInfo = new TextureCreateInfo(SearchWidth,
                SearchHeight,
                1,
                1,
                1,
                1,
                1,
                1,
                Format.R8Unorm,
                DepthStencilMode.Depth,
                Target.Texture2D,
                SwizzleComponent.Red,
                SwizzleComponent.Green,
                SwizzleComponent.Blue,
                SwizzleComponent.Alpha);

            _areaTexture = _renderer.CreateTexture(areaInfo, 1) as TextureView;
            _searchTexture = _renderer.CreateTexture(searchInfo, 1) as TextureView;

            _areaTexture.SetData(AreaTexture);
            _searchTexture.SetData(SearchTexBytes);
        }

        public TextureView Run(TextureView view, CommandBufferScoped cbs, int width, int height)
        {
            if (_recreatePipelines || _outputTexture == null || _outputTexture.Info.Width != view.Width || _outputTexture.Info.Height != view.Height)
            {
                RecreateShaders(view.Width, view.Height);
                _outputTexture?.Dispose();
                _edgeOutputTexture?.Dispose();
                _blendOutputTexture?.Dispose();
                _outputTexture = _renderer.CreateTexture(view.Info, view.ScaleFactor) as TextureView;
                _edgeOutputTexture = _renderer.CreateTexture(view.Info, view.ScaleFactor) as TextureView;
                _blendOutputTexture = _renderer.CreateTexture(view.Info, view.ScaleFactor) as TextureView;
            }

            Span<GAL.Viewport> viewports = stackalloc GAL.Viewport[1];

            viewports[0] = new GAL.Viewport(
                new Rectangle<float>(0, 0, view.Width, view.Height),
                ViewportSwizzle.PositiveX,
                ViewportSwizzle.PositiveY,
                ViewportSwizzle.PositiveZ,
                ViewportSwizzle.PositiveW,
                0f,
                1f);

            Span<Rectangle<int>> scissors = stackalloc Rectangle<int>[1];

            scissors[0] = new Rectangle<int>(0, 0, view.Width, view.Height);

            _renderer.HelperShader.Clear(_renderer,
                _edgeOutputTexture.GetImageView(),
                new float[] { 0, 0, 0, 1 },
                (uint)(ColorComponentFlags.ColorComponentRBit | ColorComponentFlags.ColorComponentGBit | ColorComponentFlags.ColorComponentBBit | ColorComponentFlags.ColorComponentABit),
                view.Width,
                view.Height,
                _edgeOutputTexture.VkFormat,
                scissors[0]
                );

            _renderer.HelperShader.Clear(_renderer,
                _blendOutputTexture.GetImageView(),
                new float[] { 0, 0, 0, 1 },
                (uint)(ColorComponentFlags.ColorComponentRBit | ColorComponentFlags.ColorComponentGBit | ColorComponentFlags.ColorComponentBBit | ColorComponentFlags.ColorComponentABit),
                view.Width,
                view.Height,
                _blendOutputTexture.VkFormat,
                scissors[0]
                );

            var dispatchX = (view.Width + IPostProcessingEffect.LocalGroupSize - 1) / IPostProcessingEffect.LocalGroupSize;
            var dispatchY = (view.Height + IPostProcessingEffect.LocalGroupSize - 1) / IPostProcessingEffect.LocalGroupSize;

            // Edge pass
            _edgePipeline.SetCommandBuffer(cbs);
            _edgePipeline.SetProgram(_edgeProgram);
            _edgePipeline.SetTextureAndSampler(ShaderStage.Compute, 1, view, _samplerLinear);

            var resolutionBuffer = new ReadOnlySpan<float>(new float[] { view.Width, view.Height });
            int rangeSize = resolutionBuffer.Length * sizeof(float);
            var bufferHandle = _renderer.BufferManager.CreateWithHandle(_renderer, rangeSize, false);

            _renderer.BufferManager.SetData(bufferHandle, 0, resolutionBuffer);
            Span<BufferRange> bufferRanges = stackalloc BufferRange[1];
            bufferRanges[0] = new BufferRange(bufferHandle, 0, rangeSize);
            _edgePipeline.SetUniformBuffers(2, bufferRanges);
            _edgePipeline.SetScissors(scissors);
            _edgePipeline.SetViewports(viewports, false);
            _edgePipeline.SetImage(0, _edgeOutputTexture, GAL.Format.R8G8B8A8Unorm);
            _edgePipeline.DispatchCompute(dispatchX, dispatchY, 1);
            _edgePipeline.ComputeBarrier();

            // Blend pass
            _blendPipeline.SetCommandBuffer(cbs);
            _blendPipeline.SetProgram(_blendProgram);
            _blendPipeline.SetTextureAndSampler(ShaderStage.Compute, 1, _edgeOutputTexture, _samplerLinear);
            _blendPipeline.SetTextureAndSampler(ShaderStage.Compute, 3, _areaTexture, _samplerLinear);
            _blendPipeline.SetTextureAndSampler(ShaderStage.Compute, 4, _searchTexture, _samplerLinear);
            _blendPipeline.SetUniformBuffers(2, bufferRanges);
            _blendPipeline.SetScissors(scissors);
            _blendPipeline.SetViewports(viewports, false);
            _blendPipeline.SetImage(0, _blendOutputTexture, GAL.Format.R8G8B8A8Unorm);
            _blendPipeline.DispatchCompute(dispatchX, dispatchY, 1);
            _blendPipeline.ComputeBarrier();

            // Neighbour pass
            _neighbourPipleline.SetCommandBuffer(cbs);
            _neighbourPipleline.SetProgram(_neighbourProgram);
            _neighbourPipleline.SetTextureAndSampler(ShaderStage.Compute, 3, _blendOutputTexture, _samplerLinear);
            _neighbourPipleline.SetTextureAndSampler(ShaderStage.Compute, 1, view, _samplerLinear);
            _neighbourPipleline.SetUniformBuffers(2, bufferRanges);
            _neighbourPipleline.SetScissors(scissors);
            _neighbourPipleline.SetViewports(viewports, false);
            _neighbourPipleline.SetImage(0, _outputTexture, GAL.Format.R8G8B8A8Unorm);
            _neighbourPipleline.DispatchCompute(dispatchX, dispatchY, 1);
            _neighbourPipleline.ComputeBarrier();

            _renderer.BufferManager.Delete(bufferHandle);

            return _outputTexture;
        }
    }
}