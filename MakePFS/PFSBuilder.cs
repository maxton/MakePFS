﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.Design.Serialization;
using System.IO;
using System.Linq;

namespace MakePFS
{
  /// <summary>
  /// Contains the functionality to construct a PFS disk image.
  /// </summary>
  public class PfsBuilder
  {
    static int CeilDiv(int a, int b) => a/b + (a%b == 0 ? 0 : 1);
    static long CeilDiv(long a, long b) => a/b + (a%b == 0 ? 0 : 1);

    private PfsHeader hdr;
    private List<PfsDinode32> inodes;
    private List<List<PfsDirent>> dirents;
    private List<PfsDirent> super_root_dirents;

    private PfsDinode32 super_root_ino, fpt_ino;

    private FSDir root;

    private List<FSDir> allDirs;
    private List<FSFile> allFiles;
    private List<FSNode> allNodes;

    private FlatPathTable fpt;

    /// <summary>
    /// Builds and saves a PFS image.
    /// </summary>
    /// <param name="p"></param>
    public void BuildPfs(PfsProperties p)
    {
      // TODO: Combine the superroot-specific stuff with the rest of the data block writing.
      // I think this is as simple as adding superroot and flat_path_table to allNodes

      hdr = new PfsHeader { BlockSize = p.BlockSize };
      inodes = new List<PfsDinode32>();
      dirents = new List<List<PfsDirent>>();

      Console.WriteLine("Setting up root structure...");
      SetupRootStructure();
      BuildFSTree(root, p.RootDirectory);
      allDirs = root.GetAllChildrenDirs();
      allFiles = root.GetAllChildrenFiles();
      allNodes = new List<FSNode>(allDirs);
      allNodes.AddRange(allFiles);

      Console.WriteLine("Creating directory inodes ({0})...", allDirs.Count);
      addDirInodes();

      Console.WriteLine("Creating file inodes ({0})...", allFiles.Count);
      addFileInodes();

      Console.WriteLine("Creating flat_path_table...");
      fpt = new FlatPathTable(allNodes);


      Console.WriteLine("Calculating data block layout...");
      allNodes.Insert(0, root);
      CalculateDataBlockLayout();

      Console.WriteLine("Writing image file...");
      hdr.Ndblock = allFiles.Sum((f) => CeilDiv(f.Size, hdr.BlockSize));
      using (var stream = File.Create(p.ImageFilename))
      {
        Console.WriteLine("Writing header...");
        hdr.WriteToStream(stream);
        Console.WriteLine("Writing inodes...");
        WriteInodes(stream);
        Console.WriteLine("Writing superroot dirents");
        WriteSuperrootDirents(stream);

        Console.WriteLine("Writing flat_path_table");
        stream.Position = fpt_ino.db[0]*hdr.BlockSize;
        fpt.WriteToStream(stream);

        Console.WriteLine("Writing data blocks...");
        for (var x = 0; x < allNodes.Count; x++)
        {
          var f = allNodes[x];
          stream.Position = f.ino.db[0] * hdr.BlockSize;
          WriteFSNode(stream, f);
        }
      }
    }

    /// <summary>
    /// Adds inodes for each dir.
    /// </summary>
    void addDirInodes()
    {
      inodes.Add(root.ino);
      foreach (var dir in allDirs)
      {
        var ino = new PfsDinode32
        {
          Mode = InodeMode.dir | InodeMode.rwx,
          Number = (uint)inodes.Count,
          Blocks = 1,
          Size = 65536
        };
        dir.ino = ino;
        dir.Dirents.Add(new PfsDirent { Name = ".", InodeNumber = ino.Number, Type = 4 });
        dir.Dirents.Add(new PfsDirent { Name = "..", InodeNumber = dir.Parent.ino.Number, Type = 5 });
        dirents.Add(dir.Dirents);
        var dirent = new PfsDirent { Name = dir.name, InodeNumber = (uint)inodes.Count, Type = 3 };
        dir.Parent.Dirents.Add(dirent);
        dir.Parent.ino.Nlink++;
        inodes.Add(ino);
      }
    }

    /// <summary>
    /// Adds inodes for each file.
    /// </summary>
    void addFileInodes()
    {
      foreach (var file in allFiles)
      {
        var ino = new PfsDinode32
        {
          Mode = InodeMode.file | InodeMode.rwx,
          Size = file.Size,
          SizeCompressed = file.Size,
          Number = (uint)inodes.Count,
          Blocks = (uint)CeilDiv(file.Size, hdr.BlockSize)
        };
        file.ino = ino;
        var dirent = new PfsDirent { Name = file.name, Type = 2, InodeNumber = (uint)inodes.Count };
        file.Parent.Dirents.Add(dirent);
        inodes.Add(ino);
      }
    }

    long dirSizeToSize(long size) => CeilDiv(size, hdr.BlockSize)*hdr.BlockSize;

    /// <summary>
    /// Sets the data blocks. Also updates header for total number of data blocks.
    /// </summary>
    void CalculateDataBlockLayout()
    {
      var inodesPerBlock = hdr.BlockSize / PfsDinode32.SizeOf;
      hdr.DinodeCount = inodes.Count;
      hdr.DinodeBlockCount = CeilDiv(inodes.Count, inodesPerBlock);
      super_root_ino.db[0] = (int)(hdr.DinodeBlockCount + 1);

      // flat path table
      fpt_ino.db[0] = super_root_ino.db[0] + 1;
      fpt_ino.Size = fpt.Size;
      fpt_ino.SizeCompressed = fpt.Size;
      fpt_ino.Blocks = (uint)CeilDiv(fpt.Size, hdr.BlockSize);
      for (int i = 1; i < fpt_ino.Blocks && i < fpt_ino.db.Length; i++)
        fpt_ino.db[i] = -1;

      // All fs entries.
      var currentBlock = fpt_ino.db[0] + fpt_ino.Blocks;
      hdr.Ndblock = 0;
      // Calculate length of all dirent blocks
      foreach (var n in allNodes)
      {
        var blocks = CeilDiv(n.Size, hdr.BlockSize);
        n.ino.db[0] = (int)currentBlock;
        n.ino.Blocks = (uint)blocks;
        n.ino.Size = n is FSDir ? dirSizeToSize(n.Size) : n.Size;
        n.ino.SizeCompressed = n.ino.Size;
        for (int i = 1; i < blocks && i < n.ino.db.Length; i++)
        {
          n.ino.db[i] = -1;
        }
        currentBlock += blocks;
        hdr.Ndblock += blocks;
      }
    }

    /// <summary>
    /// Creates inodes and dirents for superroot, flat_path_table, and uroot.
    /// Also, creates the root node for the FS tree.
    /// </summary>
    void SetupRootStructure()
    {
      inodes.Add(super_root_ino = new PfsDinode32
      {
        Mode = InodeMode.dir | InodeMode.rx_only | InodeMode.super, Blocks = 1, Size = 65536, SizeCompressed = 65536, Nlink = 1, Number = 0
      });
      inodes.Add(fpt_ino = new PfsDinode32
      {
        Mode = InodeMode.file | InodeMode.rwx | InodeMode.super, Blocks = 1, Number = 1
      });
      var uroot_ino = new PfsDinode32 { Mode = InodeMode.dir | InodeMode.rwx, Number = 2, Size = 65536, SizeCompressed = 65536, Blocks = 1 };

      super_root_dirents = new List<PfsDirent>
      {
        new PfsDirent { InodeNumber = 1, Name = "flat_path_table", Type = 2 },
        new PfsDirent { InodeNumber = 2, Name = "uroot", Type = 3 }
      };

      root = new FSDir
      {
        name = "uroot",
        ino = uroot_ino,
        Dirents = new List<PfsDirent>
        {
          new PfsDirent { Name = ".", Type = 4, InodeNumber = 2 },
          new PfsDirent { Name = "..", Type = 5, InodeNumber = 2 }
        }
      };
    }

    /// <summary>
    /// Takes a directory and a root node, and recursively makes a filesystem tree.
    /// </summary>
    /// <param name="root"></param>
    /// <param name="rootDir"></param>
    void BuildFSTree(FSDir root, string rootDir)
    {
      foreach (var d in Directory.EnumerateDirectories(rootDir))
      {
        FSDir dir;
        root.Dirs.Add(dir = new FSDir { name = Path.GetFileName(d), Parent = root });
        BuildFSTree(dir, d);
      }

      foreach (var f in Directory.EnumerateFiles(rootDir))
      {
        root.Files.Add(new FSFile
        {
          Parent = root,
          name = Path.GetFileName(f),
          OrigFileName = f,
          Size = new FileInfo(f).Length
        });
      }
    }

    /// <summary>
    /// Writes all the inodes to the image file. 
    /// </summary>
    /// <param name="s"></param>
    void WriteInodes(Stream s)
    {
      s.Position = hdr.BlockSize;
      foreach (var di in inodes)
      {
        di.WriteToStream(s);
        if (s.Position%hdr.BlockSize > hdr.BlockSize - PfsDinode32.SizeOf)
        {
          s.Position += hdr.BlockSize - (s.Position%hdr.BlockSize);
        }
      }
    }
    
    /// <summary>
    /// Writes the dirents for the superroot, which precede the flat_path_table.
    /// </summary>
    /// <param name="stream"></param>
    void WriteSuperrootDirents(Stream stream)
    {
      stream.Position = hdr.BlockSize * (hdr.DinodeBlockCount + 1);
      foreach (var d in super_root_dirents)
      {
        d.WriteToStream(stream);
      }
    }

    /// <summary>
    /// Writes all the data blocks.
    /// </summary>
    /// <param name="s"></param>
    void WriteFSNode(Stream s, FSNode f)
    {
      if (f is FSDir)
      {
        var dir = (FSDir)f;
        var startBlock = f.ino.db[0];
        foreach (var d in dir.Dirents)
        {
          d.WriteToStream(s);
          if (s.Position%hdr.BlockSize > hdr.BlockSize - PfsDirent.MaxSize)
          {
            s.Position = (++startBlock*hdr.BlockSize);
          }
        }
      }
      else if(f is FSFile)
      {
        var file = (FSFile)f;
        using (var fileStream = File.OpenRead(file.OrigFileName))
          fileStream.CopyTo(s);
      }
    }
  }
}