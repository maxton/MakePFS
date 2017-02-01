# MakePFS
A utility to create PFS (Playstation File System) images.

### Usage: 
```
makepfs -o pfs_image_name.dat -r /path/to/root/directory [-b BLOCKSIZE]
```

### Options:
* `-o filename` : Sets the output filename for the image to `filename`.
* `-r /path/to/root` : Sets the directory used as the root of the filesystem.
* `-b BLOCKSIZE` : (Optional) Sets the blocksize of the PFS image. Default is 65536.