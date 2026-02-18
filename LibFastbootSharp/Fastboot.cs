using System.Text;
using LibUsbDotNet;
using LibUsbDotNet.Main;
using LibSparseSharp;

namespace LibFastbootSharp;

/// <summary>
/// Fastboot 主类，包含与 Fastboot 设备通信的方法。
/// </summary>
public class Fastboot
{
    private const int UsbVid = 0x18D1;
    private const int UsbPid = 0xD00D;
    private const int HeaderSize = 4;
    private const int DefaultSendBlockSize = 1024 * 1024; // 1 MB, matches SharpFastboot's OnceSendDataSize

    public int Timeout { get; set; } = 3000;
    public int ReadTimeoutSeconds { get; set; } = 30;
    public int SendBlockSize { get; set; } = DefaultSendBlockSize;

    private UsbDevice? _device;
    private readonly string? _targetSerialNumber;

    public event EventHandler<StatusMessageEventArgs>? ReceivedInfo;
    public event EventHandler<(long Sent, long Total)>? SendDataProgressChanged;
    public event EventHandler<string>? CurrentStepChanged;

    public enum Status
    {
        Fail,
        Okay,
        Data,
        Info,
        Timeout,
        Unknown,
        Text
    }

    public class StatusMessageEventArgs(Status status, string? payload = null, string? text = null) : EventArgs
    {
        public Status Status { get; set; } = status;
        public string? Payload { get; set; } = payload;
        public string? Text { get; set; } = text;
    }

    public class Response(Status status)
    {
        public Status Status { get; set; } = status;
        public string Payload { get; set; } = "";
        public List<string> InfoList { get; set; } = [];
        public string Text { get; set; } = "";
        public int DataSize { get; set; }

        public Response ThrowIfError()
        {
            return Status == Status.Fail
                ? throw new Exception($"Fastboot command failed: {Payload}")
                : Status == Status.Timeout ? throw new TimeoutException("Fastboot command timed out")
                : Status == Status.Unknown ? throw new Exception("Fastboot command returned unknown status") : this;
        }
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
            "TEXT" => Status.Text,
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
            if (allDevices.Any(x => x.Vid == UsbVid && x.Pid == UsbPid))
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
            ? new UsbDeviceFinder(UsbVid, UsbPid)
            : new UsbDeviceFinder(UsbVid, UsbPid, _targetSerialNumber);

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
        writeEndpoint.Write(command, Timeout, out var wrAct);
        return wrAct != command.Length ? throw new Exception($"Failed to write command!") : ReadResponse();
    }

    public Response Command(string command) => Command(Encoding.ASCII.GetBytes(command));

    public Response RawCommand(string command) => Command(command);

    /// <summary>
    /// 读取设备响应。
    /// </summary>
    public Response ReadResponse()
    {
        if (_device == null)
        {
            throw new InvalidOperationException("Not connected to device.");
        }

        var readEndpoint = _device.OpenEndpointReader(ReadEndpointID.Ep01);
        var response = new Response(Status.Unknown);
        var buffer = new byte[64];
        var readStart = DateTime.Now;

        while (true)
        {
            readEndpoint.Read(buffer, Timeout, out var rdAct);
            if (rdAct < HeaderSize)
            {
                if ((DateTime.Now - readStart) >= TimeSpan.FromSeconds(ReadTimeoutSeconds))
                {
                    response.Status = Status.Timeout;
                    return response;
                }
                continue;
            }

            var strHeader = Encoding.ASCII.GetString(buffer, 0, HeaderSize);
            var status = GetStatusFromString(strHeader);
            var chunk = Encoding.ASCII.GetString(buffer, HeaderSize, rdAct - HeaderSize).Replace("\0", "").Trim();

            if (status == Status.Info)
            {
                response.InfoList.Add(chunk);
                ReceivedInfo?.Invoke(this, new StatusMessageEventArgs(status, chunk));
                readStart = DateTime.Now;
            }
            else if (status == Status.Text)
            {
                response.Text += chunk;
                ReceivedInfo?.Invoke(this, new StatusMessageEventArgs(status, null, chunk));
                readStart = DateTime.Now;
            }
            else if (status == Status.Data)
            {
                response.Status = Status.Data;
                response.DataSize = int.Parse(chunk, System.Globalization.NumberStyles.HexNumber);
                return response;
            }
            else
            {
                response.Status = status;
                response.Payload = chunk;
                return response;
            }
        }
    }

    public Response HandleResponse() => ReadResponse();

    public Response Download(Stream stream, long length) => DownloadData(stream, length, onEvent: true);

    public Response DownloadData(byte[] data)
    {
        using var ms = new MemoryStream(data);
        return DownloadData(ms, data.Length, onEvent: true);
    }

    public Response DownloadData(Stream stream, long length, bool onEvent = true)
    {
        if (_device == null)
        {
            throw new InvalidOperationException("Not connected to device.");
        }

        var writeEndpoint = _device.OpenEndpointWriter(WriteEndpointID.Ep01);
        var res = Command($"download:{length:x8}");
        if (res.Status != Status.Data)
        {
            return res;
        }

        var blockSize = SendBlockSize <= 0 ? DefaultSendBlockSize : SendBlockSize;
        var buffer = new byte[blockSize];
        var remaining = length;
        long sent = 0;

        while (remaining > 0)
        {
            var toRead = (int)Math.Min(blockSize, remaining);
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
            sent += read;
            if (onEvent)
            {
                SendDataProgressChanged?.Invoke(this, (sent, length));
            }
        }

        return ReadResponse();
    }

    public Response Download(byte[] data) => DownloadData(data);

    public Response UploadData(Stream stream) => Download(stream, stream.Length);

    public Response UploadData(string filePath)
    {
        using var fs = File.OpenRead(filePath);
        return Download(fs, fs.Length);
    }

    public string GetVar(string key) => Command($"getvar:{key}").ThrowIfError().Payload;

    public Dictionary<string, string> GetVarAll()
    {
        var response = Command("getvar:all").ThrowIfError();
        return response.InfoList
            .Select(str =>
            {
                var index = str.LastIndexOf(':');
                if (index == -1)
                {
                    return new { Key = str, Value = "" };
                }

                return new { Key = str[..index].Trim(), Value = str[(index + 1)..].Trim() };
            })
            .ToDictionary(x => x.Key, x => x.Value);
    }

    public int GetSlotCount()
    {
        var countStr = GetVar("slot-count");
        return int.TryParse(countStr, out var count) ? count : 1;
    }

    public string GetCurrentSlot() => GetVar("current-slot");

    public Response SetActiveSlot(string slot) => Command($"set_active:{slot}").ThrowIfError();

    public Response ErasePartition(string partition) => Command($"erase:{partition}").ThrowIfError();

    public Response Reboot(string target = "") => Command(string.IsNullOrWhiteSpace(target) ? "reboot" : $"reboot:{target}");

    public Response FlashUnsparseImage(string partition, Stream stream, long length)
    {
        CurrentStepChanged?.Invoke(this, $"Sending {partition}");
        DownloadData(stream, length).ThrowIfError();
        CurrentStepChanged?.Invoke(this, $"Flashing {partition}");
        return Command($"flash:{partition}").ThrowIfError();
    }

    public Response Flash(string partition, string filePath)
    {
        CurrentStepChanged?.Invoke(this, $"Analyzing {partition}");

        // Check if it's a sparse image
        if (SparseImageValidator.IsSparseImage(filePath))
        {
            return FlashSparse(partition, filePath);
        }

        using var fs = File.OpenRead(filePath);
        return FlashUnsparseImage(partition, fs, fs.Length);
    }

    public Response FlashSparse(string partition, string filePath)
    {
        var maxDownloadSizeStr = GetVar("max-download-size");
        var maxDownloadSize = long.Parse(maxDownloadSizeStr.TrimStart('0', 'x'), System.Globalization.NumberStyles.HexNumber);

        using var sfile = SparseFile.FromImageFile(filePath);
        var parts = sfile.Resparse((int)maxDownloadSize);
        var response = new Response(Status.Okay);

        for (var i = 0; i < parts.Count; i++)
        {
            var item = parts[i];
            using var stream = item.GetExportStream(0, item.Header.TotalBlocks);

            CurrentStepChanged?.Invoke(this, $"Sending {partition} ({i + 1}/{parts.Count})");
            Download(stream, stream.Length).ThrowIfError();

            CurrentStepChanged?.Invoke(this, $"Flashing {partition} ({i + 1}/{parts.Count})");
            response = Command($"flash:{partition}").ThrowIfError();
        }

        return response;
    }

    public Response FlashSparseImage(string partition, string filePath) => FlashSparse(partition, filePath);

    public static string[] GetDevices()
    {
        var devices = new List<string>();
        foreach (UsbRegistry usbRegistry in UsbDevice.AllDevices)
        {
            if (usbRegistry.Vid == UsbVid && usbRegistry.Pid == UsbPid)
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
