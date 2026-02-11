using System.CommandLine;
using LibSparseSharp;

var superImageArg = new Argument<FileInfo>("super_image") { Description = "Path to the super image file." };
var outputDirArg = new Argument<DirectoryInfo>("output_dir") { Description = "Output directory (default is current directory)." };
outputDirArg.SetDefaultValue(new DirectoryInfo("."));

var rootCommand = new RootCommand("Command-line tool for extracting partition images from super image.")
        {
            superImageArg,
            outputDirArg
        };

rootCommand.SetHandler((superImage, outputDir) =>
{
    if (!superImage.Exists)
    {
        Console.Error.WriteLine($"Error: File '{superImage.FullName}' does not exist.");
        return;
    }

    try
    {
        Console.WriteLine($"Unpacking '{superImage.FullName}' to '{outputDir.FullName}'...");
        SparseImageConverter.UnpackSuper(superImage.FullName, outputDir.FullName);
        Console.WriteLine("Done.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
    }
}, superImageArg, outputDirArg);

return await rootCommand.InvokeAsync(args);