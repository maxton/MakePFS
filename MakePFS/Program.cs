using System;
using System.IO;

namespace MakePFS
{
  class Program
  {
    static void Main(string[] args)
    {
      Console.WriteLine("makepfs - by maxton, 2017");
      try
      {
        var p = ParseArgs(args);
        var builder = new PfsBuilder();
        builder.BuildPfs(p);
      }
      catch (ArgumentException e)
      {
        PrintUsage();
      }
      catch (Exception e)
      {
        Console.WriteLine("Error: " + e.Message);
        Console.WriteLine(e.StackTrace);
      }
    }

    static PfsProperties ParseArgs(string[] args)
    {
      var p = new PfsProperties()
      {
        BlockSize = 0x10000,
        RootDirectory = null
      };

      for (var i = 0; i < args.Length; i++)
      {
        switch (args[i])
        {
          case "-r":
            p.RootDirectory = args[++i];
            break;
          case "-b":
            p.BlockSize = uint.Parse(args[++i]);
            break;
          case "-o":
            p.ImageFilename = args[++i];
            break;
          default:
            Console.WriteLine("Unknown argument "+args[i]);
            throw new ArgumentException();
        }
      }

      if (!Directory.Exists(p.RootDirectory))
      {
        throw new ArgumentException();
      }

      return p;
    }

    static void PrintUsage()
    {
      Console.WriteLine("Usage: makepfs -o pfs_image_name.dat -r /path/to/root/directory [-b BLOCKSIZE]");
      Console.WriteLine("If BLOCKSIZE is not specified, a default of 65536 is used.");
      Console.WriteLine();
    }
    
  }
}
