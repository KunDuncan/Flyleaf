﻿using System;
using System.Collections.Generic;
using System.Threading;

using FFmpeg.AutoGen;
using static FFmpeg.AutoGen.ffmpeg;

using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Vortice;

using FlyleafLib.MediaFramework.MediaDecoder;
using FlyleafLib.MediaFramework.MediaFrame;

using static FlyleafLib.Logger;

using ID3D11Texture2D = Vortice.Direct3D11.ID3D11Texture2D;

namespace FlyleafLib.MediaFramework.MediaRenderer;

unsafe public partial class Renderer
{
    static string[] pixelOffsets = new[] { "r", "g", "b", "a" };
    enum PSDefines { HDR, YUV }
    enum PSCase : int
    {
        None,
        HWD3D11VP,
        HWD3D11VPZeroCopy,
        HW,
        HWZeroCopy,

        RGBPacked,
        RGBPacked2,
        RGBPlanar,

        YUVPacked,
        YUVSemiPlanar,
        YUVPlanar,
        SwsScale
    }

    bool    checkHDR;
    PSCase  curPSCase;
    string  curPSUniqueId;
    float   curRatio = 1.0f; 
    string  prevPSUniqueId;

    Texture2DDescription[]          textDesc = new Texture2DDescription[4];
    ShaderResourceViewDescription[] srvDesc  = new ShaderResourceViewDescription[4];

    void InitPS()
    {
        for (int i=0; i<textDesc.Length; i++)
        {
            textDesc[i].Usage               = ResourceUsage.Default;
            textDesc[i].BindFlags           = BindFlags.ShaderResource;// | BindFlags.RenderTarget;
            textDesc[i].SampleDescription   = new SampleDescription(1, 0);
            textDesc[i].ArraySize           = 1;
            textDesc[i].MipLevels           = 1;
        }

        for (int i=0; i<textDesc.Length; i++)
        {
            srvDesc[i].Texture2D        = new() { MipLevels = 1, MostDetailedMip = 0 };
            srvDesc[i].Texture2DArray   = new Texture2DArrayShaderResourceView() { ArraySize = 1, MipLevels = 1 };
        }
    }

    internal bool ConfigPlanes(bool isNewInput = true)
    {
        bool error = false;

        try
        {
            Monitor.Enter(VideoDecoder.lockCodecCtx);
            Monitor.Enter(lockDevice);

            if (Disposed || VideoStream == null)
                return false;

            VideoDecoder.DisposeFrame(LastFrame);

            if (isNewInput)
            {
                if ((VideoStream.PixelFormatDesc->flags & AV_PIX_FMT_FLAG_BE) != 0)
                {
                    Log.Error($"{VideoStream.PixelFormatStr} not supported (BE)");
                    return false;
                }

                hdrData     = null;
                curRatio    = VideoStream.AspectRatio.Value;
                IsHDR       = VideoStream.ColorSpace == ColorSpace.BT2020;
                VideoRect   = new RawRect(0, 0, VideoStream.Width, VideoStream.Height);
            }

            var oldVP = videoProcessor;
            VideoProcessor = !VideoDecoder.VideoAccelerated || D3D11VPFailed || Config.Video.VideoProcessor == VideoProcessors.Flyleaf || (Config.Video.VideoProcessor == VideoProcessors.Auto && isHDR && !Config.Video.Deinterlace) ? VideoProcessors.Flyleaf : VideoProcessors.D3D11;

            textDesc[0].BindFlags &= ~BindFlags.RenderTarget; // Only D3D11VP without ZeroCopy requires it
            checkHDR = false;
            curPSCase = PSCase.None;
            prevPSUniqueId = curPSUniqueId;
            curPSUniqueId = "";

            Log.Debug($"Preparing planes for {VideoStream.PixelFormatStr} with {videoProcessor}");
            
            if (videoProcessor == VideoProcessors.D3D11)
            {
                if (oldVP != videoProcessor)
                {
                    VideoDecoder.DisposeFrames();
                    Config.Video.Filters[VideoFilters.Brightness].Value = Config.Video.Filters[VideoFilters.Brightness].DefaultValue;
                    Config.Video.Filters[VideoFilters.Contrast].Value = Config.Video.Filters[VideoFilters.Contrast].DefaultValue;
                }

                inputColorSpace = new()
                {
                    Usage           = 0,
                    RGB_Range       = VideoStream.AVStream->codecpar->color_range == AVColorRange.AVCOL_RANGE_JPEG ? (uint) 0 : 1,
                    YCbCr_Matrix    = VideoStream.ColorSpace != ColorSpace.BT601 ? (uint) 1 : 0,
                    YCbCr_xvYCC     = 0,
                    Nominal_Range   = VideoStream.AVStream->codecpar->color_range == AVColorRange.AVCOL_RANGE_JPEG ? (uint) 2 : 1
                };

                vpov?.Dispose();
                vd1.CreateVideoProcessorOutputView(backBuffer, vpe, vpovd, out vpov);
                vc.VideoProcessorSetStreamColorSpace(vp, 0, inputColorSpace);
                vc.VideoProcessorSetOutputColorSpace(vp, outputColorSpace);

                if (VideoDecoder.ZeroCopy)
                    curPSCase = PSCase.HWD3D11VPZeroCopy;
                else
                {
                    curPSCase = PSCase.HWD3D11VP;

                    textDesc[0].BindFlags |= BindFlags.RenderTarget;

                    textDesc[0].Width   = VideoStream.Width;
                    textDesc[0].Height  = VideoStream.Height;
                    textDesc[0].Format  = VideoDecoder.textureFFmpeg.Description.Format;
                }
            }
            else if (!Config.Video.SwsForce || VideoDecoder.VideoAccelerated) // FlyleafVP
            {
                List<string> defines = new();

                if (oldVP != videoProcessor)
                {
                    VideoDecoder.DisposeFrames();
                    Config.Video.Filters[VideoFilters.Brightness].Value = Config.Video.Filters[VideoFilters.Brightness].Minimum + ((Config.Video.Filters[VideoFilters.Brightness].Maximum - Config.Video.Filters[VideoFilters.Brightness].Minimum) / 2);
                    Config.Video.Filters[VideoFilters.Contrast].Value   = Config.Video.Filters[VideoFilters.Contrast].Minimum + ((Config.Video.Filters[VideoFilters.Contrast].Maximum - Config.Video.Filters[VideoFilters.Contrast].Minimum) / 2);
                }

                if (IsHDR)
                {
                    checkHDR = true;
                    curPSUniqueId += "h";
                    defines.Add(PSDefines.HDR.ToString());
                    psBufferData.coefsIndex = 0;
                    UpdateHDRtoSDR(hdrData, false);
                }
                else
                    psBufferData.coefsIndex = VideoStream.ColorSpace == ColorSpace.BT709 ? 1 : 2;

                for (int i=0; i<srvDesc.Length; i++)
                    srvDesc[i].ViewDimension = ShaderResourceViewDimension.Texture2D;

                // 1. HW Decoding
                if (VideoDecoder.VideoAccelerated)
                {
                    defines.Add(PSDefines.YUV.ToString());

                    if (VideoDecoder.VideoStream.PixelComp0Depth > 8)
                    {
                        srvDesc[0].Format = Format.R16_UNorm;
                        srvDesc[1].Format = Format.R16G16_UNorm;
                    }
                    else
                    {
                        srvDesc[0].Format = Format.R8_UNorm;
                        srvDesc[1].Format = Format.R8G8_UNorm;
                    }

                    if (VideoDecoder.ZeroCopy)
                    {
                        curPSCase = PSCase.HWZeroCopy;
                        curPSUniqueId += ((int)curPSCase).ToString();

                        for (int i=0; i<srvDesc.Length; i++)
                            srvDesc[i].ViewDimension = ShaderResourceViewDimension.Texture2DArray;
                    }
                    else
                    {
                        curPSCase = PSCase.HW;
                        curPSUniqueId += ((int)curPSCase).ToString();

                        textDesc[0].Width   = VideoStream.Width;
                        textDesc[0].Height  = VideoStream.Height;
                        textDesc[0].Format  = VideoDecoder.textureFFmpeg.Description.Format;
                    }

                    SetPS(curPSUniqueId, @"
color = float4(
    Texture1.Sample(Sampler, input.Texture).r, 
    Texture2.Sample(Sampler, input.Texture).rg,
    1.0);
", defines);
                }

                else if (VideoStream.IsRGB)
                {
                    // [RGB0]32 | [RGBA]32 | [RGBA]64
                    if (VideoStream.PixelPlanes == 1 && (
                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_0RGB    ||
                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_RGB0    ||
                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_0BGR    ||
                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_BGR0    ||

                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_ARGB    ||
                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_RGBA    ||
                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_ABGR    ||
                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_BGRA    ||

                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_RGBA64LE||
                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_BGRA64LE))
                    {
                        curPSCase = PSCase.RGBPacked;
                        curPSUniqueId += ((int)curPSCase).ToString();

                        textDesc[0].Width   = VideoStream.Width;
                        textDesc[0].Height  = VideoStream.Height;

                        if (VideoStream.PixelComp0Depth > 8)
                        {
                            curPSUniqueId += "x";
                            textDesc[0].Format  = srvDesc[0].Format = Format.R16G16B16A16_UNorm;
                        }
                        else if (VideoStream.PixelComp0Depth > 4)
                            textDesc[0].Format  = srvDesc[0].Format = Format.R8G8B8A8_UNorm; // B8G8R8X8_UNorm for 0[rgb]?

                        string offsets = "";
                        for (int i=0; i<3; i++)
                            offsets += pixelOffsets[(int) (VideoStream.PixelComps[i].offset / Math.Ceiling(VideoStream.PixelComp0Depth / 8.0))];

                        curPSUniqueId += offsets;

                        SetPS(curPSUniqueId, $"color = float4(Texture1.Sample(Sampler, input.Texture).{offsets}, 1.0);");
                    }
                        
                    // [BGR/RGB]16
                    else if (VideoStream.PixelPlanes == 1 && (
                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_RGB444LE||
                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_BGR444LE))
                    {
                        curPSCase = PSCase.RGBPacked2;
                        curPSUniqueId += ((int)curPSCase).ToString();

                        textDesc[0].Width   = VideoStream.Width;
                        textDesc[0].Height  = VideoStream.Height;

                        textDesc[0].Format  = srvDesc[0].Format = Format.B4G4R4A4_UNorm;

                        if (VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_RGB444LE)
                        {
                            curPSUniqueId += "a";
                            SetPS(curPSUniqueId, $"color = float4(Texture1.Sample(Sampler, input.Texture).rgb, 1.0);");
                        }
                        else
                        {
                            curPSUniqueId += "b";
                            SetPS(curPSUniqueId, $"color = float4(Texture1.Sample(Sampler, input.Texture).bgr, 1.0);");
                        }
                    }

                    // GBR(A) <=16
                    else if (VideoStream.PixelPlanes > 2 && VideoStream.PixelComp0Depth <= 16)
                    {
                        curPSCase = PSCase.RGBPlanar;
                        curPSUniqueId += ((int)curPSCase).ToString();

                        for (int i=0; i<VideoStream.PixelPlanes; i++)
                        {
                            textDesc[i].Width   = VideoStream.Width;
                            textDesc[i].Height  = VideoStream.Height;
                        }

                        string shader = @"
    color.g = Texture1.Sample(Sampler, input.Texture).r;
    color.b = Texture2.Sample(Sampler, input.Texture).r;
    color.r = Texture3.Sample(Sampler, input.Texture).r;
";

                        if (VideoStream.PixelPlanes == 4)
                        {
                            curPSUniqueId += "x";
                        
                            shader += @"
    color.a = Texture4.Sample(Sampler, input.Texture).r;
";
                        }

                        if (VideoStream.PixelComp0Depth > 8)
                        {
                            curPSUniqueId += "a";

                            for (int i=0; i<VideoStream.PixelPlanes; i++)
                                textDesc[i].Format = srvDesc[i].Format = Format.R16_UNorm;

                            shader += @"
    color = color * pow(2, " + (16 - VideoStream.PixelComp0Depth) + @");
";
                        }
                        else
                        {
                            curPSUniqueId += "b";

                            for (int i=0; i<VideoStream.PixelPlanes; i++)
                                textDesc[i].Format = srvDesc[i].Format = Format.R8_UNorm;
                        }

                        SetPS(curPSUniqueId, shader + @"
    color.a = 1;
", defines);
                    }
                }

                else // YUV
                {
                    defines.Add(PSDefines.YUV.ToString());

                    if (VideoStream.PixelPlanes == 1 && (
                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_Y210LE  || // Not tested
                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_YUYV422 ||
                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_YVYU422 ||
                        VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_UYVY422 ))
                    {
                        curPSCase = PSCase.YUVPacked;
                        curPSUniqueId += ((int)curPSCase).ToString();

                        psBufferData.texWidth = 1.0f / (VideoStream.Width >> 1);
                        textDesc[0].Width   = VideoStream.Width;
                        textDesc[0].Height  = VideoStream.Height;

                        if (VideoStream.PixelComp0Depth > 8)
                        {
                            curPSUniqueId += $"{VideoStream.Width}_";
                            textDesc[0].Format  = Format.Y210;
                            srvDesc[0].Format   = Format.R16G16B16A16_UNorm;
                        }
                        else
                        {
                            curPSUniqueId += $"{VideoStream.Width}";
                            textDesc[0].Format  = Format.YUY2;
                            srvDesc[0].Format   = Format.R8G8B8A8_UNorm;
                        }

                        string header = @"
    float  posx = input.Texture.x - (texWidth * 0.25);
    float  fx = frac(posx / texWidth);
    float  pos1 = posx + ((0.5 - fx) * texWidth);
    float  pos2 = posx + ((1.5 - fx) * texWidth);

    float4 c1 = Texture1.Sample(Sampler, float2(pos1, input.Texture.y));
    float4 c2 = Texture1.Sample(Sampler, float2(pos2, input.Texture.y));

";
                        if (VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_YUYV422 ||
                            VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_Y210LE)
                        {
                            curPSUniqueId += $"a";

                            SetPS(curPSUniqueId, header + @"
    float  leftY    = lerp(c1.r, c1.b, fx * 2);
    float  rightY   = lerp(c1.b, c2.r, fx * 2 - 1);
    float2 outUV    = lerp(c1.ga, c2.ga, fx);
    float  outY     = lerp(leftY, rightY, step(0.5, fx));
    color = float4(outY, outUV, 1.0);
", defines);
                        } else if (VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_YVYU422)
                        {
                            curPSUniqueId += $"b";

                            SetPS(curPSUniqueId, header + @"
    float  leftY    = lerp(c1.r, c1.b, fx * 2);
    float  rightY   = lerp(c1.b, c2.r, fx * 2 - 1);
    float2 outUV    = lerp(c1.ag, c2.ag, fx);
    float  outY     = lerp(leftY, rightY, step(0.5, fx));
    color = float4(outY, outUV, 1.0);
", defines);
                        } else if (VideoStream.PixelFormat == AVPixelFormat.AV_PIX_FMT_UYVY422)
                        {
                            curPSUniqueId += $"c";

                            SetPS(curPSUniqueId, header + @"
    float  leftY    = lerp(c1.g, c1.a, fx * 2);
    float  rightY   = lerp(c1.a, c2.g, fx * 2 - 1);
    float2 outUV    = lerp(c1.rb, c2.rb, fx);
    float  outY     = lerp(leftY, rightY, step(0.5, fx));
    color = float4(outY, outUV, 1.0);
", defines);
                        }
                    }
                        
                    // Y_UV | nv12,nv21,nv24,nv42,p010le,p016le,p410le,p416le | (log2_chroma_w != log2_chroma_h / Interleaved) (? nv16,nv20le,p210le,p216le)
                    // This covers all planes == 2 YUV (Semi-Planar)
                    else if (VideoStream.PixelPlanes == 2) // && VideoStream.PixelSameDepth) && !VideoStream.PixelInterleaved)
                    {
                        curPSCase = PSCase.YUVSemiPlanar;
                        curPSUniqueId += ((int)curPSCase).ToString();

                        textDesc[0].Width   = VideoStream.Width;
                        textDesc[0].Height  = VideoStream.Height;
                        textDesc[1].Width   = VideoStream.Width >> VideoStream.PixelFormatDesc->log2_chroma_w;
                        textDesc[1].Height  = VideoStream.Height >> VideoStream.PixelFormatDesc->log2_chroma_h;

                        string offsets = VideoStream.PixelComps[1].offset > VideoStream.PixelComps[2].offset ? "gr" : "rg";

                        if (VideoStream.PixelComp0Depth > 8)
                        {
                            curPSUniqueId += "x";
                            textDesc[0].Format  = srvDesc[0].Format = Format.R16_UNorm;
                            textDesc[1].Format  = srvDesc[1].Format = Format.R16G16_UNorm;
                        }
                        else
                        {
                            textDesc[0].Format = srvDesc[0].Format = Format.R8_UNorm;
                            textDesc[1].Format = srvDesc[1].Format = Format.R8G8_UNorm;
                        }

                        SetPS(curPSUniqueId, @"
color = float4(
    Texture1.Sample(Sampler, input.Texture).r, 
    Texture2.Sample(Sampler, input.Texture)." + offsets + @",
    1.0);
", defines);
                    }
                        
                    // Y_U_V
                    else if (VideoStream.PixelPlanes > 2)
                    {
                        curPSCase = PSCase.YUVPlanar;
                        curPSUniqueId += ((int)curPSCase).ToString();

                        textDesc[0].Width   = textDesc[3].Width = VideoStream.Width;
                        textDesc[0].Height  = textDesc[3].Height= VideoStream.Height;
                        textDesc[1].Width   = textDesc[2].Width = VideoStream.Width  >> VideoStream.PixelFormatDesc->log2_chroma_w;
                        textDesc[1].Height  = textDesc[2].Height= VideoStream.Height >> VideoStream.PixelFormatDesc->log2_chroma_h;

                        string shader = @"
    color.r = Texture1.Sample(Sampler, input.Texture).r;
    color.g = Texture2.Sample(Sampler, input.Texture).r;
    color.b = Texture3.Sample(Sampler, input.Texture).r;
";

                        if (VideoStream.PixelPlanes == 4)
                        {
                            curPSUniqueId += "x";
                        
                            shader += @"
    color.a = Texture4.Sample(Sampler, input.Texture).r;
";
                        }

                        if (VideoStream.PixelComp0Depth > 8)
                        {
                            curPSUniqueId += "a";

                            for (int i=0; i<VideoStream.PixelPlanes; i++)
                                textDesc[i].Format = srvDesc[i].Format = Format.R16_UNorm;

                            shader += @"
    color = color * pow(2, " + (16 - VideoStream.PixelComp0Depth) + @");
";
                        }
                        else
                        {
                            curPSUniqueId += "b";

                            for (int i=0; i<VideoStream.PixelPlanes; i++)
                                textDesc[i].Format = srvDesc[i].Format = Format.R8_UNorm;
                        }

                        SetPS(curPSUniqueId, shader + @"
    color.a = 1;
", defines);
                    }
                }
            }

            if (textDesc[0].Format != Format.Unknown && !Device.CheckFormatSupport(textDesc[0].Format).HasFlag(FormatSupport.Texture2D))
            {
                Log.Warn($"GPU does not support {textDesc[0].Format} texture format");
                curPSCase = PSCase.None;
            }

            if (curPSCase == PSCase.None)
            {
                Log.Warn($"{VideoStream.PixelFormatStr} not supported. Falling back to SwsScale");

                if (!VideoDecoder.SetupSws())
                {
                    Log.Error($"SwsScale setup failed");
                    return false;
                }

                curPSCase = PSCase.SwsScale;
                curPSUniqueId = ((int)curPSCase).ToString();

                textDesc[0].Width   = VideoStream.Width;
                textDesc[0].Height  = VideoStream.Height;
                textDesc[0].Format  = srvDesc[0].Format = Format.R8G8B8A8_UNorm;
                srvDesc[0].ViewDimension = ShaderResourceViewDimension.Texture2D;

                // TODO: should add HDR?
                SetPS(curPSUniqueId, @"
color = float4(Texture1.Sample(Sampler, input.Texture).rgb, 1.0);
");
            }

            Log.Debug($"Prepared planes for {VideoStream.PixelFormatStr} with {videoProcessor} [{curPSCase}]");

            return true;

        }
        catch (Exception e)
        {
            Log.Error($"{VideoStream.PixelFormatStr} not supported? ({e.Message}");
            error = true;
            return false;

        }
        finally
        {
            if (!error && curPSCase != PSCase.None)
            {
                context.UpdateSubresource(psBufferData, psBuffer);

                if (ControlHandle != IntPtr.Zero || SwapChainWinUIClbk != null)
                    SetViewport();
                else
                    PrepareForExtract();
            }
            Monitor.Exit(lockDevice);
            Monitor.Exit(VideoDecoder.lockCodecCtx);
        }
    }
    internal VideoFrame FillPlanes(AVFrame* frame)
    {
        try
        {
            VideoFrame mFrame = new();
            mFrame.timestamp = (long)(frame->pts * VideoStream.Timebase) - VideoDecoder.Demuxer.StartTime;
            if (CanTrace) Log.Trace($"Processes {Utils.TicksToTime(mFrame.timestamp)}");

            if (checkHDR && hdrData == null && frame->side_data != null && *frame->side_data != null)
            {
                checkHDR = false;
                var sideData = *frame->side_data;
                if (sideData->type == AVFrameSideDataType.AV_FRAME_DATA_MASTERING_DISPLAY_METADATA)
                {
                    hdrData = (AVMasteringDisplayMetadata*)sideData->data;
                    UpdateHDRtoSDR(hdrData);
                }
            }

            if (curPSCase == PSCase.HWD3D11VPZeroCopy)
            {
                mFrame.subresource  = (int) frame->data[1];
                mFrame.bufRef       = av_buffer_ref(frame->buf[0]);
            }

            else if (curPSCase == PSCase.HWD3D11VP)
            {
                mFrame.textures     = new ID3D11Texture2D[1];
                mFrame.textures[0]  = Device.CreateTexture2D(textDesc[0]);
                context.CopySubresourceRegion(
                    mFrame.textures[0], 0, 0, 0, 0, // dst
                    VideoDecoder.textureFFmpeg, (int) frame->data[1],  // src
                    new Box(0, 0, 0, textDesc[0].Width, textDesc[0].Height, 1)); // crop decoder's padding
            }

            else if (curPSCase == PSCase.HWZeroCopy)
            {
                mFrame.srvs         = new ID3D11ShaderResourceView[2];
                mFrame.bufRef       = av_buffer_ref(frame->buf[0]);
                srvDesc[0].Texture2DArray.FirstArraySlice = srvDesc[1].Texture2DArray.FirstArraySlice = (int) frame->data[1];

                mFrame.srvs[0]      = Device.CreateShaderResourceView(VideoDecoder.textureFFmpeg, srvDesc[0]);
                mFrame.srvs[1]      = Device.CreateShaderResourceView(VideoDecoder.textureFFmpeg, srvDesc[1]);
            }

            else if (curPSCase == PSCase.HW)
            {
                mFrame.textures     = new ID3D11Texture2D[1];
                mFrame.srvs         = new ID3D11ShaderResourceView[2];

                mFrame.textures[0]  = Device.CreateTexture2D(textDesc[0]);
                context.CopySubresourceRegion(
                    mFrame.textures[0], 0, 0, 0, 0, // dst
                    VideoDecoder.textureFFmpeg, (int) frame->data[1],  // src
                    new Box(0, 0, 0, textDesc[0].Width, textDesc[0].Height, 1)); // crop decoder's padding
                            
                mFrame.srvs[0]      = Device.CreateShaderResourceView(mFrame.textures[0], srvDesc[0]);
                mFrame.srvs[1]      = Device.CreateShaderResourceView(mFrame.textures[0], srvDesc[1]);
            }

            else if (curPSCase == PSCase.SwsScale)
            {
                mFrame.textures     = new ID3D11Texture2D[1];
                mFrame.srvs         = new ID3D11ShaderResourceView[1];

                sws_scale(VideoDecoder.swsCtx, frame->data, frame->linesize, 0, frame->height, VideoDecoder.swsData, VideoDecoder.swsLineSize);

                SubresourceData db  = new()
                {
                    DataPointer     = (IntPtr) VideoDecoder.swsData[0],
                    RowPitch        = VideoDecoder.swsLineSize[0]
                };
                mFrame.textures[0]  = Device.CreateTexture2D(textDesc[0], new SubresourceData[] { db });
                mFrame.srvs[0]      = Device.CreateShaderResourceView(mFrame.textures[0], srvDesc[0]);
            }

            else
            {
                mFrame.textures = new ID3D11Texture2D[VideoStream.PixelPlanes];
                mFrame.srvs     = new ID3D11ShaderResourceView[VideoStream.PixelPlanes];

                for (uint i = 0; i < VideoStream.PixelPlanes; i++)
                {
                    SubresourceData db  = new()
                    {
                        DataPointer = (IntPtr) frame->data[i],
                        RowPitch    = frame->linesize[i]
                    };

                    mFrame.textures[i]  = Device.CreateTexture2D(textDesc[i], new SubresourceData[] { db });
                    mFrame.srvs[i]      = Device.CreateShaderResourceView(mFrame.textures[i], srvDesc[i]);
                }
            }

            return mFrame;
        }
        catch (Exception e)
        {
            Log.Error($"Failed to process frame ({e.Message})");
            return null; 
        }
        finally
        {
            av_frame_unref(frame);
        }
    }

    void SetPS(string uniqueId, string sampleHLSL, List<string> defines = null)
    {
        if (curPSUniqueId == prevPSUniqueId)
            return;

        ShaderPS?.Dispose();
        ShaderPS = ShaderCompiler.CompilePS(Device, uniqueId, sampleHLSL, defines);
        context.PSSetShader(ShaderPS);
    }
}
