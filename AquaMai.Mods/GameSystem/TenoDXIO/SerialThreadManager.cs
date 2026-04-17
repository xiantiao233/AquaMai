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
                        serialPort = new SerialPort(TenoDXIO.COMPort, TenoDXIO.BaudRate);
                        serialPort.ReadTimeout = 100;
                        serialPort.Open();
                        MelonLogger.Msg($"[TenoDXIO] 成功连接输入串口: {TenoDXIO.COMPort} @ {TenoDXIO.BaudRate} bps");

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
                                        channels[i] = (ushort)(buffer[1 + i * 2] | (buffer[2 + i * 2] << 8));
                                    }

                                    // 将解包后的 34 通道数据推给触摸处理器
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