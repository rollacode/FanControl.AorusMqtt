// DiagnosticTool — Fase 2k: teste mínimo dos dois modos do pump (estado limpo)
// Roda DEPOIS de: GCC Balanced → fechar GCC → esperar 30s
// Execute como ADMINISTRADOR. Rode com: dotnet run -c Release
using HidSharp;

const int VendorId  = 0x1044;
const int ProductId = 0x7A4D;

var dev = DeviceList.Local.GetHidDevices(VendorId, ProductId).FirstOrDefault();
if (dev is null) { Console.WriteLine("Dispositivo não encontrado."); return; }

int inLen  = dev.GetMaxInputReportLength();
int outLen = dev.GetMaxOutputReportLength();
Console.WriteLine($"Encontrado: {dev.GetFriendlyName()}");

using var s = dev.Open();
s.ReadTimeout  = 2000;
s.WriteTimeout = 1000;

int ReadPump()
{
    try
    {
        var c = new byte[outLen]; c[0] = 0x99; c[1] = 0xDA;
        s.Write(c);
        var r = new byte[inLen];
        int n = s.Read(r, 0, r.Length);
        if (n < 0x08 || r[0] != 0x99) return -1;
        return (r[0x06] << 8) | r[0x05];
    }
    catch { return -1; }
}

void SendMode(byte m1, byte m2)
{
    var c1 = new byte[outLen]; c1[0]=0x99; c1[1]=0xE5; c1[2]=0x01; c1[3]=m1;
    var c2 = new byte[outLen]; c2[0]=0x99; c2[1]=0xE5; c2[2]=0x02; c2[3]=m2;
    var c3 = new byte[outLen]; c3[0]=0x99; c3[1]=0xB6;
    try { s.Write(c1); Thread.Sleep(15); s.Write(c2); Thread.Sleep(15); s.Write(c3); } catch { }
}

// Lê pump 5x e mostra todos os valores
void ShowPump(string label)
{
    var v = new List<int>();
    for (int i = 0; i < 5; i++) { Thread.Sleep(900); int r = ReadPump(); if (r > 0) v.Add(r); }
    v.Sort();
    int med = v.Count > 0 ? v[v.Count/2] : -1;
    Console.WriteLine($"  {label}: [{string.Join(", ", v)}]  mediana={med}");
}

// Estado inicial
Console.WriteLine("\n=== ESTADO INICIAL ===");
ShowPump("baseline");

// Turbo
Console.WriteLine("\n=== TURBO: E5:01:05 + E5:02:00 + B6 ===");
SendMode(0x05, 0x00);
Thread.Sleep(3000);
ShowPump("3s após Turbo");
Thread.Sleep(3000);
ShowPump("6s após Turbo");

// Volta ao Balanced antes de testar LOW
Console.WriteLine("\n  [resetando para Balanced 3s...]");
SendMode(0x00, 0x04);
Thread.Sleep(3000);
ShowPump("após reset");

// Balanced (LOW)
Console.WriteLine("\n=== BALANCED: E5:01:06 + E5:02:04 + B6 ===");
SendMode(0x06, 0x04);
Thread.Sleep(3000);
ShowPump("3s após Balanced");
Thread.Sleep(3000);
ShowPump("6s após Balanced");

// Alternância 3x com reset entre elas
Console.WriteLine("\n=== ALTERNÂNCIA HIGH/LOW com reset entre elas ===");
for (int i = 0; i < 3; i++)
{
    SendMode(0x05, 0x00); Thread.Sleep(5000);
    int h = ReadPump();
    SendMode(0x00, 0x04); Thread.Sleep(2000);
    SendMode(0x06, 0x04); Thread.Sleep(5000);
    int l = ReadPump();
    Console.WriteLine($"  Ciclo {i+1}: HIGH={h}  LOW={l}  delta={h-l}");
}

// Restaurar
SendMode(0x06, 0x04);
Thread.Sleep(1000);
Console.WriteLine($"\nFinal: {ReadPump()} RPM");
Console.WriteLine("\nPressione ENTER para sair.");
Console.ReadLine();
