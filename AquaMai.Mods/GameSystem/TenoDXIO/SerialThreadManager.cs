using System;
using System.Collections.Generic;
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

        // ================= 滤波器状态 =================
        private static float[] filterHistory = new float[34];
        private static bool isFirstFrame = true;

        // 新增：配置下发防抖时间戳
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

        // ==========================================
        // 新增：向主控板下发 34 通道硬件配置
        // ==========================================
        private static void SendHardwareConfig()
        {
            // 防抖机制：1秒内最多下发1次，防止被 STM32 60Hz的请求包撑爆串口
            if ((DateTime.Now - lastConfigSendTime).TotalSeconds < 1.0) return;
            lastConfigSendTime = DateTime.Now;

            byte[] frame = new byte[139];
            frame[0] = 0xAA; // 帧头
            frame[1] = 0x01; // 配置指令码

            // 动态读取我们写在 TenoDXIO.cs 顶部的全局配置硬编码参数
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
            List<byte> buffer = new List<byte>();
            byte[] readBuf = new byte[4096];

            while (isRunning)
            {
                if (serialPort == null || !serialPort.IsOpen)
                {
                    try
                    {
                        // 波特率硬编码为 115200
                        serialPort = new SerialPort(TenoDXIO.COMPort, 115200);
                        serialPort.ReadTimeout = 100;
                        serialPort.Open();
                        MelonLogger.Msg($"[TenoDXIO] 成功连接输入串口: {TenoDXIO.COMPort} @ 115200 bps");

                        isFirstFrame = true;
                        TouchStateProcessor.ResetCalibration();

                        // 连接断开后缓冲区会有脏数据，重连时强制清空
                        buffer.Clear();
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
                        int bytesRead = serialPort.Read(readBuf, 0, readBuf.Length);
                        for (int i = 0; i < bytesRead; i++) buffer.Add(readBuf[i]);

                        while (buffer.Count >= 70)
                        {
                            // 【核心修复】：必须先校验 Checksum，再根据状态字分支处理！
                            // 千万不要一上来就强制判断 buffer[0] == 0x00
                            int checksum = 0;
                            for (int i = 0; i < 69; i++) checksum += buffer[i];

                            if ((checksum & 0xFF) == buffer[69])
                            {
                                byte status = buffer[0];

                                if (status == 0x00) // 正常扫描数据
                                {
                                    ushort[] channels = new ushort[34];
                                    for (int i = 0; i < 34; i++)
                                    {
                                        ushort raw = (ushort)(buffer[1 + i * 2] | (buffer[2 + i * 2] << 8));

                                        // IIR 滤波逻辑
                                        if (TenoDXIO.IIRFilterFactor > 1)
                                        {
                                            if (isFirstFrame) filterHistory[i] = raw;
                                            else filterHistory[i] += (raw - filterHistory[i]) / (float)TenoDXIO.IIRFilterFactor;

                                            channels[i] = (ushort)Math.Round(filterHistory[i]);
                                        }
                                        else
                                        {
                                            channels[i] = raw;
                                        }
                                    }

                                    if (isFirstFrame && TenoDXIO.IIRFilterFactor > 1) isFirstFrame = false;

                                    TouchStateProcessor.ProcessFrame(channels);
                                }
                                else if (status == 0x01)
                                {
                                    // STM32 刚刚启动/重启，正在向我们要配置！
                                    SendHardwareConfig();
                                }

                                // (若 status 处于 0x02 或 0x11~0x15 之间，是 STM32 执行校准阶段)
                                // (我们不用做任何事，只需在下面把它这一帧正常剥离消费掉即可)

                                buffer.RemoveRange(0, 70); // 消费完整的一帧
                            }
                            else
                            {
                                buffer.RemoveAt(0); // 校验和不对，丢弃头部1个字节继续重新对齐找包
                            }
                        }
                    }
                    else
                    {
                        Thread.Sleep(2);
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