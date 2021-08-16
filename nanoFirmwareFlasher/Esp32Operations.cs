﻿//
// Copyright (c) .NET Foundation and Contributors
// See LICENSE file in the project root for full license information.
//

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace nanoFramework.Tools.FirmwareFlasher
{
    internal class Esp32Operations
    {
        // This is the only official ESP32 target available, so it's OK to use this as the target 
        // name whenever ESP32 is the specified platform
        private const string _esp32TargetName = "ESP32_WROOM_32";

        public static ExitCodes BackupFlash(
            EspTool tool, 
            Esp32DeviceInfo device,
            string backupPath,
            string fileName,
            VerbosityLevel verbosity)
        {
            // check for backup file without backup path
            if (!string.IsNullOrEmpty(fileName) &&
                string.IsNullOrEmpty(backupPath))
            {
                // backup file without backup path
                return ExitCodes.E9004;
            }

            // check if directory exists, if it doesn't, try to create
            if (!Directory.Exists(backupPath))
            {
                try
                {
                    Directory.CreateDirectory(backupPath);
                }
                catch
                {
                    return ExitCodes.E9002;
                }
            }

            // file name specified
            if(string.IsNullOrEmpty(fileName))
            {
                fileName = $"{device.ChipName}_0x{device.MacAddress}_{DateTime.UtcNow.ToShortDateString()}.bin";
            }

            var backupFilePath = Path.Combine(backupPath, fileName);

            // check file existence
            if (File.Exists(fileName))
            {
                try
                {
                    File.Delete(backupFilePath);
                }
                catch
                {
                    return ExitCodes.E9003;
                }
            }

            if(verbosity >= VerbosityLevel.Normal)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"Backing up the firmware to \r\n{backupFilePath}...");
                Console.ForegroundColor = ConsoleColor.White;
            }

            tool.BackupFlash(backupFilePath, device.FlashSize);

            if (verbosity > VerbosityLevel.Quiet)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"Flash backup saved to {fileName}");
            }

            return ExitCodes.OK;
        }

        internal static async System.Threading.Tasks.Task<ExitCodes> UpdateFirmwareAsync(
            EspTool espTool, 
            Esp32DeviceInfo esp32Device, 
            string targetName,
            bool updateFw,
            string fwVersion, 
            bool preview, 
            string applicationPath,
            string deploymentAddress,
            string clrFile,
            VerbosityLevel verbosity,
            PartitionTableSize? partitionTableSize)
        {
            var operationResult = ExitCodes.OK;
            uint address = 0;
            bool updateCLRfile = !string.IsNullOrEmpty(clrFile);

            // if a target name wasn't specified use the default (and only available) ESP32 target
            if (string.IsNullOrEmpty(targetName))
            {
                targetName = _esp32TargetName;
            }

            // perform sanity checks for the specified target agains the connected device details
            if(esp32Device.ChipType != "ESP32")
            {
                // connected to a device not supported
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("");
                Console.WriteLine("************************************** WARNING *************************************");
                Console.WriteLine("Seems that the device that you have connected is not supported by .NET nanoFramework");
                Console.WriteLine("Most likely it won't boot");
                Console.WriteLine("************************************************************************************");
                Console.WriteLine("");
            }

            if (targetName.Contains("ESP32_WROOM_32_V3") &&
                (esp32Device.ChipName.Contains("revision 0") ||
                esp32Device.ChipName.Contains("revision 1") ||
                esp32Device.ChipName.Contains("revision 2")))
            {
                // trying to use a target that's not compatible with the connected device 
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("");
                Console.WriteLine("************************************** WARNING *************************************");
                Console.WriteLine("Seems that you're about to use a firmware image for a revision 3 device, but the");
                Console.WriteLine($"connected device is {esp32Device.ChipName}. You should use the 'ESP32_WROOM_32' instead.");
                Console.WriteLine("************************************************************************************");
                Console.WriteLine("");
            }

            if (targetName.Contains("BLE") &&
                !esp32Device.Features.Contains(", BT,"))
            {
                // trying to use a traget with BT and the connected device doens't have support for it
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("");
                Console.WriteLine("************************************** WARNING *************************************");
                Console.WriteLine("Seems that you're about to use a firmware image that includes Bluetooth, but the");
                Console.WriteLine($"connected device does have support for it. You should use a target without BLE in the name");
                Console.WriteLine("************************************************************************************");
                Console.WriteLine("");
            }

            Esp32Firmware firmware = new Esp32Firmware(
                targetName, 
                fwVersion, 
                preview,
                partitionTableSize)
            {
                Verbosity = verbosity
            };

            // if this is updating with a local CLR file, download the package silently
            if (updateCLRfile)
            {
                // check file
                if (!File.Exists(clrFile))
                {
                    return ExitCodes.E9011;
                }

                // has to be a binary file
                if (Path.GetExtension(clrFile) != ".bin")
                {
                    return ExitCodes.E9012;
                }

                firmware.Verbosity = VerbosityLevel.Quiet;
            }

            // need to download update package?
            if (updateFw)
            {
                operationResult = await firmware.DownloadAndExtractAsync(esp32Device.FlashSize);
                
                if (operationResult != ExitCodes.OK)
                {
                    return operationResult;
                }
                // download successful
            }

            // if updating with a CRL file, need to have a new fw package
            if(updateCLRfile)
            {
                // remove the CLR file from the image
                firmware.FlashPartitions.Remove(Esp32Firmware.CLRAddress);

                // add it back with the file image from the command line option
                firmware.FlashPartitions.Add(Esp32Firmware.CLRAddress, clrFile);
            }

            // need to include application file?
            if (!string.IsNullOrEmpty(applicationPath))
            {
                // check application file
                if (File.Exists(applicationPath))
                {
                    if (!updateFw)
                    {
                        // this is a deployment operation only
                        // try parsing the deployment address from parameter
                        // need to remove the leading 0x and to specify that hexadecimal values are allowed
                        if (!uint.TryParse(deploymentAddress.Substring(2), System.Globalization.NumberStyles.AllowHexSpecifier, System.Globalization.CultureInfo.InvariantCulture, out address))
                        {
                            return ExitCodes.E9009;
                        }
                    }

                    string applicationBinary = new FileInfo(applicationPath).FullName;
                    firmware.FlashPartitions = new Dictionary<int, string>()
                    {
                        {
                            updateFw ? firmware.DeploymentPartitionAddress : (int)address,
                            applicationBinary
                        }
                    };
                }
                else
                {
                    return ExitCodes.E9008;
                }
            }

            if (verbosity >= VerbosityLevel.Normal)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.WriteLine($"Erasing flash...");
            }

            if (updateFw)
            {
                // erase flash
                operationResult = espTool.EraseFlash();
            }
            else
            {
                // erase flash segment

                // need to get deployment address here
                // length must both be multiples of the SPI flash erase sector size. This is 0x1000 (4096) bytes for supported flash chips.

                var fileStream = File.OpenRead(firmware.BootloaderPath);

                uint fileLength = (uint)Math.Ceiling((decimal)fileStream.Length / 0x1000) * 0x1000;

                operationResult = espTool.EraseFlashSegment(address, fileLength);
            }

            if (operationResult == ExitCodes.OK)
            {
                if (verbosity >= VerbosityLevel.Normal)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("OK");
                }
                else
                {
                    Console.WriteLine("");
                }

                Console.ForegroundColor = ConsoleColor.White;

                if (verbosity >= VerbosityLevel.Normal)
                {
                    Console.WriteLine($"Flashing firmware...");
                }

                // write to flash
                operationResult = espTool.WriteFlash(firmware.FlashPartitions);

                if (operationResult == ExitCodes.OK)
                {
                    if (verbosity >= VerbosityLevel.Normal)
                    {
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine("OK");
                    }
                    else
                    {
                        Console.WriteLine("");
                    }
                }

                Console.ForegroundColor = ConsoleColor.White;
            }

            return operationResult;
        }
    }
}
