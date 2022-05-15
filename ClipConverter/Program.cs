using Microsoft.Data.Sqlite;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace ClipConverter
{
    class Program
    {
        const string imageDataQuery = "SELECT ImageData FROM CanvasPreview";
        const string imageDataLengthQuery = "SELECT length(ImageData) FROM CanvasPreview";

        const int offsetStartChunkSqli = 0x34;
        const int relativeOffsetSqliteData = 0x10;

        static readonly string[] commandSwitches = new string[] { "F", "M", "?", "H", "HELP" };
        static readonly string[] flagSwitches = new string[] { };
        static readonly string[] knownSwitches = commandSwitches.Concat(flagSwitches).ToArray();

        static int Main(string[] args)
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
            var copyright = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).LegalCopyright.Replace("\u00A9", "(C)");

            var baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);

            if (args.Length < 1)
            {
                Console.Error.WriteLine("Error: Too few arguments");
                return 2;
            }

            var switches = new List<string>();

            var paramIndex = 0;
            for (var i = 0; i < args.Length; ++i)
            {
                var arg = args[i];

                if (arg.StartsWith("/") || arg.StartsWith("-"))
                    switches.Add(arg.ToUpper().Substring(1));
                else
                {
                    paramIndex = i;
                    break;
                }
            }

            if (switches.Count < 1)
            {
                Console.Error.WriteLine("Error: Too few arguments");
                return 2;
            }

            if (switches.Where(i => commandSwitches.Contains(i)).Count() > 1)
            {
                Console.Error.WriteLine("Error: Specified multiple commands");
                return 7;
            }

            if (switches.Any(i => !knownSwitches.Contains(i)))
            {
                foreach (var sw in switches.Where(i => !knownSwitches.Contains(i)))
                    Console.Error.WriteLine("Warning: Unknown command line switch: " + sw);
            }

            var command = switches.First(i => commandSwitches.Contains(i));

            var pargs = args.Skip(paramIndex).ToArray();

            if (command == "F")
            {
                if (pargs.Length < 1)
                {
                    Console.Error.WriteLine("Error: Too few arguments");
                    return 2;
                }
                if (pargs.Length > 2)
                {
                    Console.Error.WriteLine("Error: Too many arguments");
                    return 3;
                }

                var inputFile = pargs[0];
                var outputFile = pargs[1];

                if (!File.Exists(inputFile) || !FileIsReadable(inputFile))
                {
                    Console.Error.WriteLine("Error: Cannot open input file");
                    return 4;
                }

                Extract(inputFile, outputFile);
            }
            else if (command == "M")
            {
                if (pargs.Length < 2)
                {
                    Console.Error.WriteLine("Error: Too few arguments");
                    return 2;
                }
                if (pargs.Length > 3)
                {
                    Console.Error.WriteLine("Error: Too many arguments");
                    return 3;
                }

                var inputFilePath = pargs[0];
                var inputFilePattern = pargs[1];
                var outputDir = pargs[2];

                if (!Directory.Exists(inputFilePath))
                {
                    Console.Error.WriteLine("Error: Input directory does not exist");
                    return 5;
                }

                if (File.Exists(outputDir))
                {
                    Console.Error.WriteLine("Error: Output directory is a file");
                    return 6;
                }

                if (!Directory.Exists(outputDir))
                    Directory.CreateDirectory(outputDir);

                var files = Directory.GetFiles(inputFilePath, inputFilePattern);

                if (files.Length < 1)
                    Console.Error.WriteLine("Warning: No input files found");
                else
                    foreach (var file in files)
                    {
                        Console.WriteLine(file);
                        Extract(file, Path.Combine(outputDir, Path.GetFileNameWithoutExtension(file) + ".png"));
                    }
            }
            else if (command == "?" || command == "H" || command == "HELP")
            {
                Console.WriteLine("ClipConverter " + version);
                Console.WriteLine(copyright);
                Console.WriteLine();
                Console.WriteLine("Usage:");
                Console.WriteLine("clipconverter /F [Input file] [Output file]");
                Console.WriteLine("clipconverter /M [Input folder] [Input pattern] [Output folder]");
                Console.WriteLine("clipconverter [/?|/H|/HELP]");
            }
            else
            {
                Console.WriteLine("Invalid option: " + command);
                Console.WriteLine("Enter clipconverter /? to view the available options");
            }

            return 0;
        }

        private static uint SwapEndianness(uint v)
        {
            return ((v & 0x000000ff) << 24) |
                   ((v & 0x0000ff00) << 8) |
                   ((v & 0x00ff0000) >> 8) |
                   ((v & 0xff000000) >> 24);
        }

        private static void Extract(string inputFile, string outputFile)
        {
            var tempName = Path.GetTempFileName();

            using (var inputStream = File.OpenRead(inputFile))
            using (var outputStream = File.OpenWrite(outputFile))
            {

                inputStream.Seek(offsetStartChunkSqli, SeekOrigin.Begin);
                var offsetBuffer = new byte[4];
                var offset = inputStream.Read(offsetBuffer, 0, 4);
                var offsetLE = BitConverter.ToUInt32(offsetBuffer);
                var offsetBE = SwapEndianness(offsetLE);

                var index = offsetBE + relativeOffsetSqliteData;

                if (index < 0)
                    throw new Exception("SQLite header not found");


                using (var sqliteData = File.OpenWrite(tempName))
                {
                    sqliteData.Seek(0, SeekOrigin.Begin);
                    inputStream.Seek(index, SeekOrigin.Begin);
                    inputStream.CopyTo(sqliteData);
                }

                var csb = new SqliteConnectionStringBuilder();
                csb.Add("Data Source", tempName);

                var length = 0;
                byte[] buffer = new byte[0];

                using (var dbConnection = new SqliteConnection(csb.ToString()))
                {
                    dbConnection.Open();

                    using (var cmdLength = dbConnection.CreateCommand())
                    {
                        cmdLength.CommandText = imageDataLengthQuery;

                        using (var reader = cmdLength.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                length = reader.GetInt32(0);
                            }
                        }
                    }

                    using (var cmdData = dbConnection.CreateCommand())
                    {
                        cmdData.CommandText = imageDataQuery;

                        using (var reader = cmdData.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                buffer = new byte[length];

                                reader.GetBytes(0, 0, buffer, 0, length);
                            }
                        }
                    }
                }

                outputStream.Seek(0, SeekOrigin.Begin);
                using (var writer = new BinaryWriter(outputStream))
                    writer.Write(buffer);
            }
        }

        private static bool FileIsReadable(string filename)
        {
            try
            {
                File.Open(filename, FileMode.Open, FileAccess.Read).Dispose();
                return true;
            }
            catch (IOException)
            {
                return false;
            }
        }
    }
}
