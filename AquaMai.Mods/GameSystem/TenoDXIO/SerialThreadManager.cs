using System;
using System.IO.Ports;
using System.Threading;
using MelonLoader;

namespace AquaMai.Mods.GameSystem
{
    public static class SerialThreadManager
    {
        private static bool isRunning = false;
        private static Thread serialThread;
        private static SerialPort serialPort;

        // ================= 性能优化预分配 =================
        // 1. 复用通道数组，避免每帧 new ushort[34] 触发 GC 卡顿
        private static readonly ushort[] channelsCache = new ushort[34];

        // 2. 使用滑动窗口数组替换效率低下的 List<byte>
        private static readonly byte[] streamBuffer = new byte[8192];
        private static int bufferLength = 0;

        // ================= 滤波器状态 =================
        private static readonly float[] filterHistory = new float[34];
        private static bool isFirstFrame = true;
        // 新增：舍弃前100帧不稳定数据，解决 IIR 冷启动问题
        private static int warmupFrames = 100;

        // 配置下发防抖时间戳
        private static DateTime lastConfigSendTime = DateTime.MinValue;

        public static void Start()
        {
            if (isRunning) return;
            isRunning = true;
            serialThread = new Thread(SerialReaderThread) { IsBackground = true };
            serialThread.Start();
        }

        public static void Stop()
        {
            isRunning = false;
            CloseSerial();
        }

        private static void CloseSerial()
        {
            if (serialPort != null)
            {
                if (serialPort.IsOpen) { try { serialPort.Close(); } catch { } }
                serialPort = null;
            }
        }

        private static void SendHardwareConfig()
        {
            if ((DateTime.Now - lastConfigSendTime).TotalSeconds < 1.0) return;
            lastConfigSendTime = DateTime.Now;

            byte[] frame = new byte[139];
            frame[0] = 0xAA;
            frame[1] = 0x01;

            for (int i = 0; i < 34; i++)
            {
                string logical = HardwareConfig.PhysicalToLogicalMap[i];
                char block = logical[0];
                var param = HardwareConfig.GetParams(block);

                frame[2 + i] = (byte)param.Res;
                frame[36 + i] = (byte)param.Mod;
                frame[70 + i] = (byte)param.Sns;
                frame[104 + i] = (byte)param.Div;
            }

            int checksum = 0;
            for (int i = 0; i < 138; i++) checksum += frame[i];
            frame[138] = (byte)(checksum & 0xFF);

            try
            {
                if (serialPort != null && serialPort.IsOpen)
                {
                    serialPort.Write(frame, 0, frame.Length);
                    MelonLogger.Msg($"[TenoDXIO] 主控重启，已向主控板下发 34 通道硬件扫描配置 ({frame.Length} bytes)");
                }
            }
            catch (Exception ex)
            {
                MelonLogger.Error($"[TenoDXIO] 配置下发失败: {ex.Message}");
            }
        }

        private static void SerialReaderThread()
        {
            while (isRunning)
            {
                if (serialPort == null || !serialPort.IsOpen)
                {
                    try
                    {
                        serialPort = new SerialPort(TenoDXIO.COMPort, 115200)
                        {
                            ReadTimeout = 100
                        };
                        serialPort.Open();
                        MelonLogger.Msg($"[TenoDXIO] 成功连接输入串口: {TenoDXIO.COMPort} @ 115200 bps");

                        // 状态重置
                        isFirstFrame = true;
                        warmupFrames = 100; // 重新连接时，再次预热 100 帧
                        bufferLength = 0;   // 清空脏数据缓冲区

                        TouchStateProcessor.ResetCalibration();
                    }
                    catch (Exception)
                    {
                        Thread.Sleep(2000);
                        continue;
                    }
                }

                try
                {
                    if (serialPort.BytesToRead > 0)
                    {
                        // 1. 直接读入滑动窗口尾部，避免中转数组
                        int bytesRead = serialPort.Read(streamBuffer, bufferLength, streamBuffer.Length - bufferLength);
                        bufferLength += bytesRead;

                        int processIndex = 0;

                        // 2. 寻找有效数据包
                        while (bufferLength - processIndex >= 70)
                        {
                            int checksum = 0;
                            for (int i = 0; i < 69; i++)
                            {
                                checksum += streamBuffer[processIndex + i];
                            }

                            // 校验通过
                            if ((checksum & 0xFF) == streamBuffer[processIndex + 69])
                            {
                                byte status = streamBuffer[processIndex + 0];

                                if (status == 0x00) // 正常扫描数据
                                {
                                    // 预热期判定：抛弃前 100 帧极端不稳定的通电电容数据
                                    if (warmupFrames > 0)
                                    {
                                        warmupFrames--;
                                    }
                                    else
                                    {
                                        for (int i = 0; i < 34; i++)
                                        {
                                            ushort raw = (ushort)(streamBuffer[processIndex + 1 + i * 2] | (streamBuffer[processIndex + 2 + i * 2] << 8));

                                            if (TenoDXIO.IIRFilterFactor > 1)
                                            {
                                                if (isFirstFrame) filterHistory[i] = raw;
                                                else filterHistory[i] += (raw - filterHistory[i]) / (float)TenoDXIO.IIRFilterFactor;

                                                channelsCache[i] = (ushort)Math.Round(filterHistory[i]);
                                            }
                                            else
                                            {
                                                channelsCache[i] = raw;
                                            }
                                        }

                                        if (isFirstFrame && TenoDXIO.IIRFilterFactor > 1) isFirstFrame = false;

                                        // 将复用的 channelsCache 传递进去
                                        TouchStateProcessor.ProcessFrame(channelsCache);
                                    }
                                }
                                else if (status == 0x01)
                                {
                                    SendHardwareConfig();
                                }

                                // 步进 70 字节 (消费掉这一帧)
                                processIndex += 70;
                            }
                            else
                            {
                                // 校验失败，只往前走 1 个字节，尝试重新对齐包头
                                processIndex++;
                            }
                        }

                        // 3. 将未处理完的残余数据统一平移到数组头部
                        if (processIndex > 0)
                        {
                            bufferLength -= processIndex;
                            if (bufferLength > 0)
                            {
                                // Array.Copy 使用底层内存块复制，极快
                                Array.Copy(streamBuffer, processIndex, streamBuffer, 0, bufferLength);
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(1); // 缩短休眠，提高高刷新率下的响应速度
                    }
                }
                catch (Exception)
                {
                    MelonLogger.Error("[TenoDXIO] 串口断开，尝试恢复中...");
                    CloseSerial();
                }
            }
            CloseSerial();
        }
    }
}