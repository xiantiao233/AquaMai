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

                        isFirstFrame = true; // 每次重连串口时，重置滤波器的首帧状态
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
                        int bytesRead = serialPort.Read(readBuf, 0, readBuf.Length);
                        for (int i = 0; i < bytesRead; i++) buffer.Add(readBuf[i]);

                        while (buffer.Count >= 70)
                        {
                            if (buffer[0] == 0x00)
                            {
                                int checksum = 0;
                                for (int i = 0; i < 69; i++) checksum += buffer[i];

                                if ((checksum & 0xFF) == buffer[69])
                                {
                                    ushort[] channels = new ushort[34];
                                    for (int i = 0; i < 34; i++)
                                    {
                                        ushort raw = (ushort)(buffer[1 + i * 2] | (buffer[2 + i * 2] << 8));

                                        // IIR 滤波逻辑
                                        if (TenoDXIO.IIRFilterFactor > 1)
                                        {
                                            if (isFirstFrame)
                                            {
                                                filterHistory[i] = raw; // 首帧直接赋值
                                            }
                                            else
                                            {
                                                // Y[n] = Y[n-1] + (X[n] - Y[n-1]) / Factor
                                                filterHistory[i] += (raw - filterHistory[i]) / (float)TenoDXIO.IIRFilterFactor;
                                            }
                                            // 四舍五入后转回 ushort
                                            channels[i] = (ushort)Math.Round(filterHistory[i]);
                                        }
                                        else
                                        {
                                            // 未开启滤波
                                            channels[i] = raw;
                                        }
                                    }

                                    if (isFirstFrame && TenoDXIO.IIRFilterFactor > 1)
                                    {
                                        isFirstFrame = false;
                                    }

                                    TouchStateProcessor.ProcessFrame(channels);
                                    buffer.RemoveRange(0, 70);
                                }
                                else
                                {
                                    buffer.RemoveAt(0);
                                }
                            }
                            else
                            {
                                buffer.RemoveAt(0);
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