﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DispatchSystem
{
    //定义委托
    public delegate void UdpReciveDelegate(string str);
    class UdpSever
    {
        //服务器IP
        public static IPAddress ipaddress;
        //服务器端口
        public static int port;
        //服务器启动停止状态，默认停止
        public static bool State = false;
        //接收及发送的数据总长度
        public static int RxLength;
        public static int TxLength;
        public static int ErrorCount;//错误次数

        //初始化设备数
        public static int DeviceNum = 20;
        //初始化寄存器数
        public static int RegisterNum = 128;
        //设备，寄存器，数据和时间戳
        public static Int64[,,] Ddata = new Int64[DeviceNum, RegisterNum, 2];
        //设备端口及IP
        public static EndPoint[] EndPointArray = new EndPoint[DeviceNum];
        //设备更新时间(最后一次收到数据时间)
        public static DateTime[] UpdateTime = new DateTime[DeviceNum];
        //服务器地址
        public static int ServerAddress = 1;
        //设备心跳周期(单位：秒)
        public static int HeartCycle = 5;

        //数据接收结构体
        struct PointData
        {
            public EndPoint endpoint;
            public byte[] recv;
            public int length;
        };

        //监听服务总进程
        static Thread threadMain;

        //函数返回结构体
        public struct Resault
        {
            public bool Reault;
            public string Message;
        }

        //


        //定义该委托的事件     
        //public static event UdpReciveDelegate UdpReciveEvent;

        /// <summary>
        /// 启动UDP服务器
        /// </summary>
        static IPEndPoint ipEndPoint;
        static Socket socket;
        public static Resault Start()
        {
            Resault rs = new Resault();
            try
            {
                ipEndPoint = new IPEndPoint(ipaddress, port);
                socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                socket.Bind(ipEndPoint);
                rs.Reault = true;
                rs.Message = "启动成功";

                threadMain = new Thread(mainFunc);
                threadMain.Start();

                //启动检测连接进程
                State = true;//更新服务器状态
                return rs;
            }
            catch (Exception ex)
            {
                rs.Reault = false;
                rs.Message = ex.Message;
                return rs;
            }
        }

        /// <summary>
        /// 停止UDP服务器
        /// </summary>
        public static Resault Stop()
        {
            Resault rs = new Resault();
            try
            {
                //注销检测连接进程
                threadMain.Abort();
                socket.Close();
                State = false;//更新服务器状态 

                rs.Reault = true;
                rs.Message = "停止成功";
                return rs;
            }
            catch (Exception ex)
            {
                rs.Reault = false;
                rs.Message = ex.Message;
                return rs;
            }

        }

        //启动监听
        private static void mainFunc()
        {
            PointData pd = new PointData();
            IPEndPoint sender = new IPEndPoint(IPAddress.Any, 0);
            EndPoint Remote = (EndPoint)(sender);
            //定义接收池字符串
            string StringBuf = string.Empty;
            while (true)
            {
                Thread.Sleep(1);
                //从缓冲区读取数据
                byte[] bytes = new byte[1000];
                int length = socket.ReceiveFrom(bytes, ref Remote);
                //更新接收到的数据总长度
                RxLength += length;
                //定义临时数组,将数据转存到临时数组
                byte[] Buf = new byte[length];
                for (int i = 0; i < length; i++)
                {
                    Buf[i] = bytes[i];
                }
                #region 数据解析
                //将byte数组转换成字符串
                string str = Encoding.ASCII.GetString(Buf);
                //清除多余的\0
                str = str.Replace("\0", "");
                Console.WriteLine(string.Format("收到数据:{0}\r\n", str));
                //将收到的数据追加到缓冲池
                StringBuf += str;
                #endregion

                int L = 0;
                //利用正则表达式截取所有有效数据
                MatchCollection mc = Regex.Matches(StringBuf, @"(?<=\$).+?(?=\!)");
                if (mc.Count > 0)//找到了有效数据
                {
                    int count = 0;
                    foreach (var item in mc)
                    {
                        count++;
                        Buf = StrToHexByte(item.ToString());
                        Console.WriteLine(string.Format("第{0}次分包解析:{1}\r\n", count, ByteToHexStr(Buf)));

                        //CRC校验
                        int crc16 = CRC.crc_16(Buf, Buf.Length - 2);
                        if (crc16 == (Buf[Buf.Length - 2] << 8 | Buf[Buf.Length - 1]))
                        {
                            //帧ID
                            int FrameID = Buf[0] << 8 | Buf[1];
                            //设备地址
                            int DeviceAddress = Buf[2] << 8 | Buf[3];
                            //帧类型
                            int FrameType = Buf[4];
                            //目标地址
                            int DstAddress = Buf[5] << 8 | Buf[6];

                            #region 判断帧类型
                            switch (FrameType)
                            {
                                case 0:
                                    #region 心跳帧
                                    Console.WriteLine(string.Format("帧类型:心跳帧，设备ID：{0}\r\n"), DeviceAddress);
                                    //存储设备端口信息到EndPointArray
                                    EndPointArray[DeviceAddress] = pd.endpoint;
                                    //更新设备响应时间
                                    UpdateTime[DeviceAddress] = DateTime.Now;
                                    #endregion
                                    break;
                                case 1:
                                    #region 操作帧
                                    Console.WriteLine(string.Format("帧类型:操作帧\r\n"));
                                    switch (Buf[7])//功能码
                                    {
                                        case 1:
                                            #region 读单个寄存器
                                            //判断目标地址是否在线
                                            if (EndPointArray[DeviceAddress] != null)
                                            {
                                                byte[] sendbyte = new byte[14];

                                                sendbyte[0] = Buf[0];//帧ID高
                                                sendbyte[1] = Buf[1];//帧ID底
                                                sendbyte[2] = (byte)(ServerAddress >> 8);//本机ID高
                                                sendbyte[3] = (byte)(ServerAddress);//本机ID低
                                                sendbyte[4] = 0x02;//帧类型
                                                sendbyte[5] = (byte)(DeviceAddress >> 8);//目标地址高
                                                sendbyte[6] = (byte)(DeviceAddress);//目标地址低
                                                sendbyte[7] = 0x01;//功能码
                                                sendbyte[8] = Buf[8];//寄存器地址高
                                                sendbyte[9] = Buf[9];//寄存器地址低

                                                sendbyte[10] = (byte)(Ddata[DeviceAddress, (Buf[8] << 8 | Buf[9]), 0] >> 8);//数据高
                                                sendbyte[11] = (byte)(Ddata[DeviceAddress, (Buf[8] << 8 | Buf[9]), 0]);//数据低

                                                int crcRes = CRC.crc_16(sendbyte, 12);

                                                sendbyte[12] = (byte)(crcRes >> 8);
                                                sendbyte[13] = (byte)(crcRes);
                                                sendToUdp(EndPointArray[DeviceAddress], sendbyte);
                                            }
                                            #endregion
                                            break;
                                        case 2:
                                            #region 写单个寄存器
                                            //判断目标地址是否在线
                                            if (EndPointArray[DeviceAddress] != null)
                                            {
                                                byte[] sendbyte = new byte[11];
                                                int RegAddress = Buf[8] << 8 | Buf[9];
                                                sendbyte[0] = Buf[0];//帧ID高
                                                sendbyte[1] = Buf[1];//帧ID底
                                                sendbyte[2] = (byte)(ServerAddress >> 8);//本机ID高
                                                sendbyte[3] = (byte)(ServerAddress);//本机ID低
                                                sendbyte[4] = 0x02;//帧类型
                                                sendbyte[5] = (byte)(DeviceAddress >> 8);//目标地址高
                                                sendbyte[6] = (byte)(DeviceAddress);//目标地址低
                                                sendbyte[7] = 0x02;//功能码

                                                if (RegAddress <= RegisterNum)
                                                {
                                                    //写入寄存器
                                                    Ddata[DeviceAddress, RegAddress, 0] = (UInt16)(Buf[10] << 8 | Buf[11]);
                                                    //更新时间戳
                                                    Ddata[DeviceAddress, RegAddress, 1] = DateTimeToStamp(DateTime.Now);
                                                    sendbyte[8] = 1;//结果
                                                }
                                                else
                                                {
                                                    sendbyte[8] = 0;//结果
                                                }

                                                int crcRes = CRC.crc_16(sendbyte, 9);

                                                sendbyte[9] = (byte)(crcRes >> 8);
                                                sendbyte[10] = (byte)(crcRes);
                                                sendToUdp(EndPointArray[DeviceAddress], sendbyte);
                                            }
                                            #endregion
                                            break;
                                        case 3:
                                            #region 读多个寄存器
                                            //判断目标地址是否在线
                                            if (EndPointArray[DeviceAddress] != null)
                                            {
                                                int Num = Buf[11];
                                                byte[] sendbyte = new byte[13 + Num * 2];
                                                //寄存器起始地址
                                                int RegStartAdd = Buf[8] << 8 | Buf[9];
                                                sendbyte[0] = Buf[0];//帧ID高
                                                sendbyte[1] = Buf[1];//帧ID底
                                                sendbyte[2] = (byte)(ServerAddress >> 8);//本机ID高
                                                sendbyte[3] = (byte)(ServerAddress);//本机ID低
                                                sendbyte[4] = 0x02;//帧类型
                                                sendbyte[5] = (byte)(DeviceAddress >> 8);//目标地址高
                                                sendbyte[6] = (byte)(DeviceAddress);//目标地址低
                                                sendbyte[7] = 0x03;//功能码
                                                sendbyte[8] = Buf[8];//寄存器起始地址高
                                                sendbyte[9] = Buf[9];//寄存器起始地址低

                                                sendbyte[10] = Buf[10];//数量

                                                for (int i = 0; i < Num; i++)
                                                {
                                                    sendbyte[11 + i * 2] = (byte)(Ddata[DeviceAddress, RegStartAdd + i, 0] >> 8);//数据高
                                                    sendbyte[12 + i * 2] = (byte)(Ddata[DeviceAddress, RegStartAdd + i, 0]);//数据低
                                                }

                                                int crcRes = CRC.crc_16(sendbyte, 11 + 2 * Num);
                                                sendbyte[11 + 2 * Num] = (byte)(crcRes >> 8);
                                                sendbyte[11 + 2 * Num + 1] = (byte)(crcRes);
                                                sendToUdp(EndPointArray[DeviceAddress], sendbyte);
                                            }
                                            #endregion
                                            break;
                                        case 4://
                                            #region 写多个寄存器
                                            //判断目标地址是否在线
                                            if (EndPointArray[DeviceAddress] != null)
                                            {
                                                byte[] sendbyte = new byte[11];
                                                int RegStartAddress = Buf[8] << 8 | Buf[9];
                                                int Num = Buf[10];
                                                sendbyte[0] = Buf[0];//帧ID高
                                                sendbyte[1] = Buf[1];//帧ID底
                                                sendbyte[2] = (byte)(ServerAddress >> 8);//本机ID高
                                                sendbyte[3] = (byte)(ServerAddress);//本机ID低
                                                sendbyte[4] = 0x02;//帧类型
                                                sendbyte[5] = (byte)(DeviceAddress >> 8);//目标地址高
                                                sendbyte[6] = (byte)(DeviceAddress);//目标地址低
                                                sendbyte[7] = 0x04;//功能码

                                                if ((RegStartAddress + Num) <= RegisterNum)
                                                {
                                                    for (int i = 0; i < Num; i++)
                                                    {
                                                        //写入寄存器
                                                        Ddata[DeviceAddress, RegStartAddress + i, 0] = (UInt16)(Buf[11 + i * 2] << 8 | Buf[12 + i * 2]);
                                                        //更新时间戳
                                                        Ddata[DeviceAddress, RegStartAddress + i, 1] = DateTimeToStamp(DateTime.Now);
                                                    }
                                                    sendbyte[8] = 1;//结果
                                                }
                                                else
                                                {
                                                    sendbyte[8] = 0;//结果
                                                }

                                                int crcRes = CRC.crc_16(sendbyte, 9);
                                                sendbyte[9] = (byte)(crcRes >> 8);
                                                sendbyte[10] = (byte)(crcRes);
                                                sendToUdp(EndPointArray[DeviceAddress], sendbyte);
                                            }
                                            #endregion
                                            break;
                                        default:
                                            ErrorCount++;
                                            Console.WriteLine(string.Format("帧类型错误：{0}\r\n", ErrorCount));
                                            length = 0;
                                            break;
                                    }
                                    #endregion
                                    break;
                                case 2:
                                    #region 响应帧
                                    Console.WriteLine(string.Format("帧类型:响应帧\r\n"));
                                    switch (Buf[5])//功能码
                                    {
                                        case 1:
                                            #region 读单个寄存器
                                            //L = 12;
                                            //tempByte = new byte[L];
                                            //for (int i = 0; i < L; i++)
                                            //{
                                            //    tempByte[i] = Buf[i];
                                            //}
                                            //length -= L;
                                            //pd = new PointData();
                                            //pd.length = L;
                                            //pd.endpoint = Remote;
                                            //pd.recv = tempByte;

                                            ////发送到子进程处理
                                            //clientThread = new Thread(new ParameterizedThreadStart(ThreadFunc));
                                            //clientThread.IsBackground = true;
                                            //clientThread.Start(pd);
                                            #endregion
                                            break;
                                        case 2:
                                            #region 写单个寄存器
                                            //L = 9;
                                            //tempByte = new byte[L];
                                            //for (int i = 0; i < L; i++)
                                            //{
                                            //    tempByte[i] = Buf[i];
                                            //}
                                            //length -= L;
                                            //pd = new PointData();
                                            //pd.length = L;
                                            //pd.endpoint = Remote;
                                            //pd.recv = tempByte;

                                            ////发送到子进程处理
                                            //clientThread = new Thread(new ParameterizedThreadStart(ThreadFunc));
                                            //clientThread.IsBackground = true;
                                            //clientThread.Start(pd);
                                            #endregion
                                            break;
                                        case 3:
                                            #region 读多个寄存器
                                            L = 13;
                                            //tempByte = new byte[L];
                                            //for (int i = 0; i < L; i++)
                                            //{
                                            //    tempByte[i] = Buf[i];
                                            //}
                                            //length -= L;
                                            //pd = new PointData();
                                            //pd.length = L;
                                            //pd.endpoint = Remote;
                                            //pd.recv = tempByte;

                                            ////发送到子进程处理
                                            //clientThread = new Thread(new ParameterizedThreadStart(ThreadFunc));
                                            //clientThread.IsBackground = true;
                                            //clientThread.Start(pd);
                                            #endregion
                                            break;
                                        case 4://
                                            #region 写多个寄存器
                                            //L = 9;
                                            //tempByte = new byte[L];
                                            //for (int i = 0; i < L; i++)
                                            //{
                                            //    tempByte[i] = Buf[i];
                                            //}
                                            //length -= L;
                                            //pd = new PointData();
                                            //pd.length = L;
                                            //pd.endpoint = Remote;
                                            //pd.recv = tempByte;

                                            ////发送到子进程处理
                                            //clientThread = new Thread(new ParameterizedThreadStart(ThreadFunc));
                                            //clientThread.IsBackground = true;
                                            //clientThread.Start(pd);
                                            #endregion
                                            break;
                                        default:
                                            // ErrorLength += length;
                                            // label_Error.Text = string.Format("错误数据：{0}", ErrorLength);
                                            length = 0;
                                            break;
                                    }
                                    #endregion
                                    break;
                                default:
                                    //出错次数加一
                                    ErrorCount++;
                                    //打印消息
                                    Console.WriteLine(string.Format("数据错误：{0}\r\n", ErrorCount));
                                    break;
                            }
                            #endregion
                        }
                    }
                    //清除已经处理的数据
                    StringBuf = StringBuf.Remove(0, StringBuf.LastIndexOf("!"));
                }

                #region 如果还有残留数据，则去掉$之前的所有数据
                //判断是否包含消息头
                if (StringBuf.Contains("$"))
                {
                    //如果消息头前面有数据则清除，只保留后面的数据
                    if (StringBuf.IndexOf("$") > 0)
                    {
                        StringBuf = StringBuf.Remove(0, StringBuf.IndexOf("$"));
                    }
                }
                else
                {
                    //没有包含消息头则清除数据
                    StringBuf = string.Empty;
                }
                #endregion
            }
        }

        //功能函数
        private static void ThreadFunc(object obj)
        {
            //try
            //{
            //Thread.Sleep(10);
            byte[] bytes = new byte[1024];
            PointData pd = (PointData)obj;
            //显示为16进制数
            //addText(byteToHexString(pd.recv) + "\r\n");
            //解析
            if (pd.length >= 7)
            {
                //1.CRC校验
                byte[] temp = new byte[pd.length - 2];
                for (int i = 0; i < pd.length - 2; i++)
                {
                    temp[i] = pd.recv[i];
                }
                int crc16 = CRC.crc_16(temp);
                if (crc16 == (pd.recv[pd.length - 2] << 8 | pd.recv[pd.length - 1]))
                {
                    //设备地址
                    int SrcAddress = (pd.recv[0] << 8 | pd.recv[1]);
                    //目标地址
                    int DstAddress = (pd.recv[3] << 8 | pd.recv[4]);

                    //if (DstAddress != ServerAddress)
                    //{
                    //    //目标地址不是本机，转发数据到目标地址
                    //    //判断目标地址是否在线
                    //    if (EndPointArray[SrcAddress] != null)
                    //    {
                    //        sendToUdp(EndPointArray[DstAddress], pd.recv);
                    //        string str = "时间：" + DateTime.Now.ToLongTimeString() + "." + DateTime.Now.Millisecond + "\r\n";
                    //        str += "设备地址：" + SrcAddress + "，目标地址：" + DstAddress + "\r\n";
                    //        str += byteToHexString(pd.recv) + "\r\n";
                    //        str += "\r\n";
                    //        // addText(str);
                    //    }
                    //    pd.recv[3] = (byte)ServerAddress;//将目标地址改为本机，更新本机数组内容
                    //}
                    //else
                    //{
                    switch (pd.recv[2])//帧类型
                    {
                        case 0:
                            #region 心跳00
                            //存储设备端口信息到EndPointArray
                            EndPointArray[SrcAddress] = pd.endpoint;
                            //更新设备响应时间
                            UpdateTime[SrcAddress] = DateTime.Now;
                            #endregion
                            break;
                        case 1:
                            #region 操作帧01
                            switch (pd.recv[5])//功能码
                            {
                                case 1:
                                    #region 读单个寄存器01  
                                    //判断目标地址是否在线
                                    if (EndPointArray[SrcAddress] != null)
                                    {
                                        byte[] sendbyte = new byte[10];

                                        sendbyte[0] = (byte)(ServerAddress >> 8);
                                        sendbyte[1] = (byte)(ServerAddress);
                                        sendbyte[2] = 0x02;
                                        sendbyte[3] = (byte)(SrcAddress >> 8);
                                        sendbyte[4] = (byte)(SrcAddress);
                                        sendbyte[5] = 0x01;
                                        sendbyte[6] = (byte)(pd.recv[6] >> 8);
                                        sendbyte[7] = (byte)(pd.recv[7]);

                                        sendbyte[8] = (byte)(Ddata[SrcAddress, (pd.recv[6] << 8 | pd.recv[7]), 0] >> 8);
                                        sendbyte[9] = (byte)(Ddata[SrcAddress, (pd.recv[6] << 8 | pd.recv[7]), 0]);
                                        int crcRes = CRC.crc_16(sendbyte);

                                        byte[] sendbyte1 = new byte[12];
                                        for (int i = 0; i < 10; i++)
                                        {
                                            sendbyte1[i++] = sendbyte[i];
                                        }

                                        sendbyte1[10] = (byte)(crcRes >> 8);
                                        sendbyte1[11] = (byte)(crcRes);
                                        sendToUdp(EndPointArray[SrcAddress], sendbyte1);
                                    }
                                    #endregion
                                    break;
                                case 2:
                                    #region 写单个寄存器02
                                    //写入寄存器
                                    Ddata[(pd.recv[0] << 8 | pd.recv[1]), (pd.recv[6] << 8 | pd.recv[7]), 0] = (UInt16)(pd.recv[8] << 8 | pd.recv[9]);

                                    //更新时间戳
                                    Ddata[(pd.recv[0] << 8 | pd.recv[1]), (pd.recv[6] << 8 | pd.recv[7]), 1] = DateTimeToStamp(DateTime.Now);

                                    //发送响应帧
                                    Respose(SrcAddress, 2);
                                    #endregion
                                    break;
                                case 3:
                                    #region 读多个寄存器03
                                    //判断目标地址是否在线
                                    if (EndPointArray[SrcAddress] != null)
                                    {
                                        byte[] sendbyte = new byte[8 + pd.recv[8] * 2];

                                        sendbyte[0] = (byte)(ServerAddress >> 8);
                                        sendbyte[1] = (byte)(ServerAddress);
                                        sendbyte[2] = 0x02;
                                        sendbyte[3] = (byte)(SrcAddress >> 8);
                                        sendbyte[4] = (byte)(SrcAddress);
                                        sendbyte[5] = 0x03;
                                        sendbyte[6] = (byte)(pd.recv[6] >> 8);
                                        sendbyte[7] = (byte)(pd.recv[7]);

                                        for (int i = 0; i < pd.recv[8]; i++)
                                        {
                                            sendbyte[8 + 2 * i] = (byte)(Ddata[SrcAddress, (pd.recv[6] << 8 | pd.recv[7]) + i, 0] >> 8);
                                            sendbyte[9 + 2 * i] = (byte)(Ddata[SrcAddress, (pd.recv[6] << 8 | pd.recv[7]) + i, 0]);
                                        }

                                        int crcRes = CRC.crc_16(sendbyte);

                                        byte[] sendbyte1 = new byte[8 + pd.recv[8] * 2 + 2];
                                        for (int i = 0; i < sendbyte.Length; i++)
                                        {
                                            sendbyte1[i++] = sendbyte[i];
                                        }

                                        sendbyte1[sendbyte.Length] = (byte)(crcRes >> 8);
                                        sendbyte1[sendbyte.Length + 1] = (byte)(crcRes);
                                        sendToUdp(EndPointArray[SrcAddress], sendbyte1);
                                    }
                                    #endregion
                                    break;
                                case 4:
                                    #region 写多个寄存器04
                                    //判断准备写的寄存器数量是否有效，未限制最大值
                                    if (pd.recv[8] > 0)
                                    {
                                        //将待写入的数据循环写入对应的寄存器地址
                                        for (int i = 0; i < pd.recv[8]; i++)
                                        {
                                            Ddata[(pd.recv[0] << 8 | pd.recv[1]), (pd.recv[6] << 8 | pd.recv[7]) + i, 0] = (UInt16)(pd.recv[9 + 2 * i] << 8 | pd.recv[10 + 2 * i]);
                                            //更新时间戳
                                            Ddata[(pd.recv[0] << 8 | pd.recv[1]), (pd.recv[6] << 8 | pd.recv[7]), 1] = DateTimeToStamp(DateTime.Now);
                                        }
                                    }
                                    #endregion
                                    break;
                                default:
                                    break;
                            }

                            #endregion
                            break;
                        case 2:
                            #region 响应帧02
                            #endregion
                            break;
                        default:
                            break;
                    }
                    // }
                }
                else
                {
                    //校验失败
                    //  addText(string.Format("功能进程，校验失败：{0}\r\n", byteToHexString(pd.recv)));

                }
                // addText("---\r\n\r\n");
            }

            //}
            //catch (Exception ex)
            //{
            //    //  addText(ex.ToString() + "\r\n");
            //    //MessageBox.Show(ex.ToString());
            //}
        }

        /// <summary>
        /// 发送响应帧
        /// </summary>
        /// <param name="dstAddress">目标地址</param>
        /// <param name="type">帧类型</param>
        private static void Respose(int dstAddress, int type)
        {
            byte[] sendbyte;
            switch (type)
            {
                case 2://写单个字节
                    {
                        sendbyte = new byte[7];
                        sendbyte[0] = (byte)(ServerAddress >> 8);
                        sendbyte[1] = (byte)(ServerAddress);
                        sendbyte[2] = 0x02;
                        sendbyte[3] = (byte)(dstAddress >> 8);
                        sendbyte[4] = (byte)(dstAddress);
                        sendbyte[5] = 0x02;
                        sendbyte[6] = 0x01;

                        int crcRes = CRC.crc_16(sendbyte);

                        byte[] sendbyte1 = new byte[9];
                        for (int i = 0; i < 7; i++)
                        {
                            sendbyte1[i] = sendbyte[i];
                        }

                        sendbyte1[7] = (byte)(crcRes >> 8);
                        sendbyte1[8] = (byte)(crcRes);
                        sendToUdp(EndPointArray[dstAddress], sendbyte1);
                        Console.WriteLine(string.Format("帧类型:响应帧 {0}\r\n", ByteToHexStr(sendbyte1)));
                    }
                    break;
                default:
                    break;
            }
        }

        /// <summary>
        /// 字节数组转10进制字符串
        /// </summary>
        public static string byteToString(byte[] bytes)
        {
            string str = string.Empty;
            if (bytes != null)
            {
                for (int i = 0; i < bytes.Length; i++)
                {
                    str += bytes[i].ToString();
                    if (i != bytes.Length - 1)
                    {
                        //str += " ";
                    }
                }
            }
            return str;
        }

        public static void writeWord(int dstAdress, int dstID, int data)
        {
            byte[] sendbyte = new byte[10];

            sendbyte[0] = (byte)(ServerAddress >> 8);
            sendbyte[1] = (byte)(ServerAddress);
            sendbyte[2] = 0x01;
            sendbyte[3] = (byte)(dstAdress >> 8);
            sendbyte[4] = (byte)(dstAdress);
            sendbyte[5] = 0x02;
            sendbyte[6] = (byte)(dstID >> 8);
            sendbyte[7] = (byte)(dstID);

            sendbyte[8] = (byte)(data >> 8);
            sendbyte[9] = (byte)(data);
            int crcRes = CRC.crc_16(sendbyte);

            byte[] sendbyte1 = new byte[12];
            for (int i = 0; i < 10; i++)
            {
                sendbyte1[i] = sendbyte[i];
            }

            sendbyte1[10] = (byte)(crcRes >> 8);
            sendbyte1[11] = (byte)(crcRes);
            sendToUdp(EndPointArray[dstAdress], sendbyte1);
        }

        /// <summary>
        /// 发送
        /// </summary>
        /// <param name="EndPort">EndPoint</param>
        /// <param name="str">byte[]</param>
        public static void sendToUdp(EndPoint EndPort, byte[] str)
        {
            if (str != null && EndPort != null)
            {
                byte[] buf = new byte[str.Length * 2 + 2];
                buf[0] = (byte)('$');
                string sting = ByteToHexStr(str);
                for (int i = 0; i < str.Length * 2; i++)
                {
                    buf[i + 1] = (byte)(sting[i]);
                }
                buf[buf.Length - 1] = (byte)('!');
                TxLength += buf.Length;
                //    labelTx.Text = string.Format("发送数据：{0}", TxLength);
                socket.SendTo(buf, EndPort);
            }
        }

        public class CRC
        {
            public static ushort crc_16(byte[] data)
            {
                uint IX, IY;
                ushort crc = 0xFFFF;//set all 1

                int len = data.Length;
                if (len <= 0)
                    crc = 0;
                else
                {
                    len--;
                    for (IX = 0; IX <= len; IX++)
                    {
                        crc = (ushort)(crc ^ (data[IX]));
                        for (IY = 0; IY <= 7; IY++)
                        {
                            if ((crc & 1) != 0)
                                crc = (ushort)((crc >> 1) ^ 0xA001);
                            else
                                crc = (ushort)(crc >> 1); //
                        }
                    }
                }

                byte buf1 = (byte)((crc & 0xff00) >> 8);//高位置
                byte buf2 = (byte)(crc & 0x00ff); //低位置

                crc = (ushort)(buf1 << 8);
                crc += buf2;
                return crc;
            }
            public static ushort crc_16(byte[] data, int len)
            {
                uint IX, IY;
                ushort crc = 0xFFFF;//set all 1

                if (len <= 0)
                    crc = 0;
                else
                {
                    len--;
                    for (IX = 0; IX <= len; IX++)
                    {
                        crc = (ushort)(crc ^ (data[IX]));
                        for (IY = 0; IY <= 7; IY++)
                        {
                            if ((crc & 1) != 0)
                                crc = (ushort)((crc >> 1) ^ 0xA001);
                            else
                                crc = (ushort)(crc >> 1); //
                        }
                    }
                }

                byte buf1 = (byte)((crc & 0xff00) >> 8);//高位置
                byte buf2 = (byte)(crc & 0x00ff); //低位置

                crc = (ushort)(buf1 << 8);
                crc += buf2;
                return crc;
            }
        }

        /// <summary>
        /// 日期转时间戳
        /// </summary>
        /// <param name="datetime">日期时间</param>
        /// <returns></returns>
        public static long DateTimeToStamp(DateTime datetime)
        {
            var start = new DateTime(1970, 1, 1, 0, 0, 0, datetime.Kind);
            return Convert.ToInt64((datetime - start).TotalSeconds);
        }

        /// <summary>
        /// 时间戳转日期
        /// </summary>
        /// <param name="timestamp">时间戳</param>
        /// <returns></returns>
        public static DateTime StampToDateTime(long timestamp)
        {
            var datetime = new DateTime();
            var start = new DateTime(1970, 1, 1, 0, 0, 0, datetime.Kind);
            return start.AddSeconds(timestamp);
        }

        /// <summary>
        /// 时间戳转日期
        /// </summary>
        /// <param name="timestamp">时间戳</param>
        /// <param name="format">输出格式，DateTimeFormatInfo</param>
        /// <returns></returns>
        public static string StampToString(long timestamp)
        {
            var datetime = new DateTime();
            var start = new DateTime(1970, 1, 1, 0, 0, 0, datetime.Kind);
            return start.AddSeconds(timestamp).ToString("yyyy-MM-dd HH:mm:ss");
        }

        #region 字符串和Byte之间的转化
        /// <summary>
        /// 数字和字节之间互转
        /// </summary>
        /// <param name="num"></param>
        /// <returns></returns>
        public static int IntToBitConverter(int num)
        {
            int temp = 0;
            byte[] bytes = BitConverter.GetBytes(num);//将int32转换为字节数组
            temp = BitConverter.ToInt32(bytes, 0);//将字节数组内容再转成int32类型
            return temp;
        }

        /// <summary>
        /// 将字符串转为16进制字符，允许中文
        /// </summary>
        /// <param name="s"></param>
        /// <param name="encode"></param>
        /// <returns></returns>
        public static string StringToHexString(string s, Encoding encode, string spanString)
        {
            byte[] b = encode.GetBytes(s);//按照指定编码将string编程字节数组
            string result = string.Empty;
            for (int i = 0; i < b.Length; i++)//逐字节变为16进制字符
            {
                result += Convert.ToString(b[i], 16) + spanString;
            }
            return result;
        }
        /// <summary>
        /// 将16进制字符串转为字符串
        /// </summary>
        /// <param name="hs"></param>
        /// <param name="encode"></param>
        /// <returns></returns>
        public static string HexStringToString(string hs, Encoding encode)
        {
            string strTemp = "";
            byte[] b = new byte[hs.Length / 2];
            for (int i = 0; i < hs.Length / 2; i++)
            {
                strTemp = hs.Substring(i * 2, 2);
                b[i] = Convert.ToByte(strTemp, 16);
            }
            //按照指定编码将字节数组变为字符串
            return encode.GetString(b);
        }
        /// <summary>
        /// byte[]转为16进制字符串
        /// </summary>
        /// <param name="bytes"></param>
        /// <returns></returns>
        public static string ByteToHexStr(byte[] bytes)
        {
            string returnStr = "";
            if (bytes != null)
            {
                for (int i = 0; i < bytes.Length; i++)
                {
                    returnStr += bytes[i].ToString("X2");
                }
            }
            return returnStr;
        }
        /// <summary>
        /// 将16进制的字符串转为byte[]
        /// </summary>
        /// <param name="hexString"></param>
        /// <returns></returns>
        public static byte[] StrToHexByte(string hexString)
        {
            hexString = hexString.Replace(" ", "");
            if ((hexString.Length % 2) != 0)
                hexString += " ";
            byte[] returnBytes = new byte[hexString.Length / 2];
            for (int i = 0; i < returnBytes.Length; i++)
                returnBytes[i] = Convert.ToByte(hexString.Substring(i * 2, 2), 16);
            return returnBytes;
        }

        #endregion
    }
}
