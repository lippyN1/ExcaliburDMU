namespace Excalibur.Core.Models;

public enum RepairMode
{
    /// <summary>Do not attempt repair. Bad sectors are only recorded.</summary>
    Ignore = 0,

    /// <summary>Re-write the original data back. Data preserved if read succeeded; 
    /// if read failed, zeros are written (data lost in that sector only).</summary>
    Refresh = 1,

    /// <summary>Write zeros over the bad sector. For ATA drives this forces controller 
    /// to remap (G-LIST). If WriteFile succeeds — remap happened. If fails — spare area exhausted.</summary>
    Remap = 2,

    /// <summary>Erase the bad sector with zeros. Same as Remap but without checking if remap happened.</summary>
    Erase = 3,
}
