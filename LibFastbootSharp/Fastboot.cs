using System.Text;
using LibUsbDotNet;
using LibUsbDotNet.Main;

namespace Potato.Fastboot;

/// <summary>
/// Fastboot 主类，包含与 Fastboot 设备通信的方法。
/// </summary>
public class Fastboot
{
    private const int USB_VID = 0x18D1;
    private const int USB_PID = 0xD00D;
    private const int HEADER_SIZE = 4;
    private const int BLOCK_SIZE = 512 * 1024; // 512 KB

    public int Timeout { get; set; } = 3000;

    private UsbDevice? _device;
    private string? _targetSerialNumber;

    public enum Status
    {
        Fail,
        Okay,
        Data,
        Info,
        Unknown
    }

    public class Response(Status status, string payload)
    {
        public Status Status { get; set; } = status;
        public string Payload { get; set; } = payload;
        public byte[]? RawData { get; set; }
    }

    public Fastboot(string serial)
    {
        _targetSerialNumber = serial;
    }

    public Fastboot()
    {
        _targetSerialNumber = null;
    }

    private Status GetStatusFromString(string header)
    {
        return header switch
        {
            "INFO" => Status.Info,
            "OKAY" => Status.Okay,
            "DATA" => Status.Data,
            "FAIL" => Status.Fail,
            _ => Status.Unknown
        };
    }

    /// <summary>
    /// 等待任意 Fastboot 设备连接。
    /// </summary>
    public void Wait()
    {
        var counter = 0;
        while (true)
        {
            var allDevices = UsbDevice.AllDevices;
            if (allDevices.Any(x => x.Vid == USB_VID && x.Pid == USB_PID))
            {
                return;
            }

            if (counter == 50)
            {
                throw new TimeoutException("Wait for device timeout.");
            }

            Thread.Sleep(500);
            counter++;
        }
    }

    /// <summary>
    /// 连接到 Fastboot 设备。
    /// </summary>
    public void Connect()
    {
        var finder = string.IsNullOrWhiteSpace(_targetSerialNumber)
            ? new UsbDeviceFinder(USB_VID, USB_PID)
            : new UsbDeviceFinder(USB_VID, USB_PID, _targetSerialNumber);

        _device = UsbDevice.OpenUsbDevice(finder) ?? throw new Exception("No devices available.");
        if (_device is IUsbDevice wDev)
        {
            wDev.SetConfiguration(1);
            wDev.ClaimInterface(0);
        }
    }

    public void Disconnect()
    {
        _device?.Close();
        _device = null;
    }

    public string? GetSerialNumber() => _device?.Info.SerialString;

    /// <summary>
    /// 发送命令并读取响应。
    /// </summary>
    public Response Command(byte[] command)
    {
        if (_device == null)
        {
            throw new InvalidOperationException("Not connected to device.");
        }

        var writeEndpoint = _device.OpenEndpointWriter(WriteEndpointID.Ep01);
        var readEndpoint = _device.OpenEndpointReader(ReadEndpointID.Ep01);

        writeEndpoint.Write(command, Timeout, out var wrAct);
        if (wrAct != command.Length)
        {
            throw new Exception($"Failed to write command!");
        }

        var status = Status.Unknown;
        var responseBuilder = new StringBuilder();
        var buffer = new byte[64];

        while (true)
        {
            readEndpoint.Read(buffer, Timeout, out var rdAct);
            if (rdAct < HEADER_SIZE)
            {
                status = Status.Unknown;
                break;
            }

            var strHeader = Encoding.ASCII.GetString(buffer, 0, HEADER_SIZE);
            status = GetStatusFromString(strHeader);

            var chunk = Encoding.ASCII.GetString(buffer, HEADER_SIZE, rdAct - HEADER_SIZE);
            responseBuilder.Append(chunk);

            if (status != Status.Info)
            {
                break;
            }
        }

        var resultPayload = responseBuilder.ToString().Replace("\r", "").Replace("\0", "");
        return new Response(status, resultPayload) { RawData = [.. buffer.Take(64)] };
    }

    public Response Command(string command) => Command(Encoding.ASCII.GetBytes(command));

    public void UploadData(string path)
    {
        using var fs = File.OpenRead(path);
        UploadData(fs);
    }

    public void UploadData(Stream stream)
    {
        if (_device == null)
        {
            throw new InvalidOperationException("Not connected to device.");
        }

        var writeEndpoint = _device.OpenEndpointWriter(WriteEndpointID.Ep01);
        var readEndpoint = _device.OpenEndpointReader(ReadEndpointID.Ep01);
        var length = stream.Length;
        var buffer = new byte[BLOCK_SIZE];

        if (Command($"download:{length:X8}").Status != Status.Data)
        {
            throw new Exception("Invalid response for data size");
        }

        var remaining = length;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(BLOCK_SIZE, remaining);
            var read = stream.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
            }

            writeEndpoint.Write(buffer, 0, read, Timeout, out var written);
            if (written != read)
            {
                throw new Exception("Failed to write data block");
            }

            remaining -= read;
        }

        var resBuffer = new byte[64];
        readEndpoint.Read(resBuffer, Timeout, out var rdAct);
        if (rdAct < HEADER_SIZE)
        {
            throw new Exception("Invalid response after upload");
        }

        var header = Encoding.ASCII.GetString(resBuffer, 0, HEADER_SIZE);
        if (GetStatusFromString(header) != Status.Okay)
        {
            throw new Exception("Invalid status after upload");
        }
    }

    public static string[] GetDevices()
    {
        var devices = new List<string>();
        foreach (UsbRegistry usbRegistry in UsbDevice.AllDevices)
        {
            if (usbRegistry.Vid == USB_VID && usbRegistry.Pid == USB_PID)
            {
                // We don't necessarily need to open to get serial string if it's available in registry,
                // but open is more reliable for checking if it's really there/accessible.
                if (usbRegistry.Open(out var dev))
                {
                    devices.Add(dev.Info.SerialString);
                    dev.Close();
                }
            }
        }
        return [.. devices];
    }
}
