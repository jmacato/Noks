using Noks.Dct3.Messaging;
namespace Noks.Cli;

public readonly record struct SstTransaction(uint Kind, uint Size, uint Addr, uint Data, uint Cycle, uint Access)
{
    public override string ToString()
    {
        string kind = Kind switch
        {
            0 => "code-read",
            1 => "data-read",
            2 => "write",
            _ => $"kind{Kind}",
        };

        return $"{kind} size={Size} addr={Addr:X8} data={Data:X8} access={Access}";
    }
}
