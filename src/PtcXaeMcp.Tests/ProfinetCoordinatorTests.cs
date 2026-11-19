using System.Text.Json;
using PtcXaeMcp.Tests.Fakes;
using PtcXaeMcp.WinForms.TwinCat;

namespace PtcXaeMcp.Tests;

/// <summary>
/// The Profinet device (RT) on an EL6631-0010 and the sync task of an I/O device: GSDML parsing, the device/box/module XML,
/// the sync-task patch of an export, and the coordinator's refusals, call order and read-backs.
/// </summary>
public sealed class ProfinetCoordinatorTests : IDisposable
{
    private const string El6631 = "TIID^Device 1 (EtherCAT)^Term 1 (EK1100)^Term 5 (EL6631-0010)";
    private const string Device = "TIID^PN Device";
    private const string Box = Device + "^Box 1";
    private const string Api = Box + "^API";
    private const string GsdmlName = "GSDML-V2.44-beckhoff-EL6631-20240715.xml";

    // A minimal GSDML in the structure of the PROFINET GSDML schema: two DAPs (the second accepts BYTE_O4 too),
    // three plain modules and one modular one, English primary texts and German ones.
    internal const string Gsdml = """
        <?xml version="1.0" encoding="utf-8"?>
        <ISO15745Profile xmlns="http://www.profibus.com/GSDML/2003/11/DeviceProfile">
          <ProfileBody>
            <ApplicationProcess>
              <DeviceAccessPointList>
                <DeviceAccessPointItem ID="DAP 1" ModuleIdentNumber="0x00000001" PhysicalSlots="0..63">
                  <ModuleInfo><Name TextId="T_DAP1"/></ModuleInfo>
                  <UseableModules>
                    <ModuleItemRef ModuleItemTarget="BIT_I8"/>
                    <ModuleItemRef ModuleItemTarget="BYTE_I4"/>
                  </UseableModules>
                </DeviceAccessPointItem>
                <DeviceAccessPointItem ID="DAP 2" ModuleIdentNumber="0x00000002" PhysicalSlots="0..255">
                  <ModuleInfo><Name TextId="T_DAP2"/></ModuleInfo>
                  <UseableModules>
                    <ModuleItemRef ModuleItemTarget="BIT_I8"/>
                    <ModuleItemRef ModuleItemTarget="BYTE_I4"/>
                    <ModuleItemRef ModuleItemTarget="BYTE_O4"/>
                    <ModuleItemRef ModuleItemTarget="MOD_X"/>
                  </UseableModules>
                </DeviceAccessPointItem>
              </DeviceAccessPointList>
              <ModuleList>
                <ModuleItem ID="BIT_I8" ModuleIdentNumber="0x00000100">
                  <ModuleInfo CategoryRef="Byte Module"><Name TextId="T_I8"/></ModuleInfo>
                  <VirtualSubmoduleList><VirtualSubmoduleItem ID="BIT_I8"><IOData>
                    <Input><DataItem DataType="Unsigned8" TextId="x"/></Input>
                  </IOData></VirtualSubmoduleItem></VirtualSubmoduleList>
                </ModuleItem>
                <ModuleItem ID="BYTE_I4" ModuleIdentNumber="0x00000706">
                  <ModuleInfo CategoryRef="Byte Module"><Name TextId="T_I4"/></ModuleInfo>
                  <VirtualSubmoduleList><VirtualSubmoduleItem ID="BYTE_I4"><IOData>
                    <Input><DataItem DataType="Unsigned16" TextId="x"/><DataItem DataType="OctetString" Length="2" TextId="x"/></Input>
                  </IOData></VirtualSubmoduleItem></VirtualSubmoduleList>
                </ModuleItem>
                <ModuleItem ID="BYTE_O4" ModuleIdentNumber="0x00000707">
                  <ModuleInfo CategoryRef="Byte Module"><Name TextId="T_O4"/></ModuleInfo>
                  <VirtualSubmoduleList><VirtualSubmoduleItem ID="BYTE_O4"><IOData>
                    <Output><DataItem DataType="Float32" TextId="x"/></Output>
                  </IOData></VirtualSubmoduleItem></VirtualSubmoduleList>
                </ModuleItem>
                <ModuleItem ID="MOD_X" ModuleIdentNumber="0x00000900">
                  <ModuleInfo><Name TextId="T_MX"/></ModuleInfo>
                  <UseableSubmodules><SubmoduleItemRef SubmoduleItemTarget="S1"/></UseableSubmodules>
                </ModuleItem>
              </ModuleList>
            </ApplicationProcess>
            <ExternalTextList>
              <PrimaryLanguage>
                <Text TextId="T_DAP1" Value="EL6631-0010 V1"/>
                <Text TextId="T_DAP2" Value="EL6631-0010 V2"/>
                <Text TextId="T_I8" Value="1 Byte Input"/>
                <Text TextId="T_I4" Value="4 Byte Input"/>
                <Text TextId="T_O4" Value="4 Byte Output"/>
                <Text TextId="T_MX" Value="Modular"/>
              </PrimaryLanguage>
              <Language xml:lang="de">
                <Text TextId="T_I4" Value="4 Byte Eingang"/>
              </Language>
            </ExternalTextList>
          </ProfileBody>
        </ISO15745Profile>
        """;

    private readonly string _dir;

    public ProfinetCoordinatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ptc-xae-mcp-tests", "profinet-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, GsdmlName), Gsdml);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static string BoxXml(uint vendor = 2, uint product = 0x19E73052, int itemId = 4) =>
        $"<TreeItem><ItemName>Term 5 (EL6631-0010)</ItemName><PathName>{El6631}</PathName><ItemType>5</ItemType><ItemId>{itemId}</ItemId>" +
        $"<EtherCAT><Slave><Info><Name>Term 5</Name><VendorId>{vendor}</VendorId><ProductCode>{product}</ProductCode>" +
        "<RevisionNo>1179658</RevisionNo><ProductRevision>EL6631-0010-0018</ProductRevision></Info></Slave></EtherCAT></TreeItem>";

    private static string DeviceXml(string path, int subType = 118, int ecatId = 0, string? ecatName = null) =>
        $"<TreeItem><ItemName>{path[5..]}</ItemName><PathName>{path}</PathName><ItemType>2</ItemType><ItemSubType>{subType}</ItemSubType>" +
        $"<DeviceDef><AddressInfo><Ecat><EtherCATDeviceId>{ecatId}</EtherCATDeviceId>" +
        (ecatName is null ? "" : $"<EtherCATDeviceName>{ecatName}</EtherCATDeviceName>") +
        "</Ecat></AddressInfo></DeviceDef><DevicePnDeviceDef><PnModules/></DevicePnDeviceDef></TreeItem>";

    private static string ExportXml(int dapIdent = 2, string ethernet = "<Ethernet DeviceDesc=\"EL6631-0010 PROFINET IO Device\"/>") => $"""
        <?xml version="1.0"?>
        <TcSmItem TcSmVersion="1.0" ClassName="CDevEthernetProtocolPnSlaveDef" SubType="118">
        <Device Id="2" DevType="118" RemoteName="PN Device">
        <Name>__FILENAME__</Name>
        <Box Id="5" BoxType="9127">
        <Name>Box 1</Name>
        <Profinet DeviceId="6631" VendorId="288" BoxOnDeviceTyp="118">
        <UsedGsdmlModule ModuleIdentNumber="{dapIdent}" ModuleName="DAP Module"><Slot>0</Slot></UsedGsdmlModule>
        <Ethernet DeviceDesc="inside the box, not the device"/>
        </Profinet>
        </Box>
        <Profinet PLCPortNr="851" AddPortCount="1">
        {ethernet}
        </Profinet>
        </Device>
        </TcSmItem>
        """;

    // An open solution with an EtherCAT master holding the EL6631-0010 and tasks PlcTask (no image) and IoTask (image, 301).
    private static FakeTwinCatSession OpenFake()
    {
        var fake = new FakeTwinCatSession { IsConnected = true, SolutionOpen = true, ConsumeXmlApplier = null };
        fake.TreeItemXml[El6631] = BoxXml();
        fake.TreeChildren["TIID"] = [new TreeChildInfo("Device 1 (EtherCAT)", 2)];
        fake.TreeItemXml["TIID^Device 1 (EtherCAT)"] = DeviceXml("TIID^Device 1 (EtherCAT)", subType: 111);
        fake.AddTask("TIRT^PlcTask", 20, 350, 100000, image: false);
        fake.AddTask("TIRT^IoTask", 21, 301, 20000, image: true);
        fake.PlcTaskLinks.Add("TIRT^PlcTask");
        return fake;
    }

    // OpenFake plus the Profinet device with its box, API and one module in slot 1, as twincat_profinet_device_add leaves it.
    private static FakeTwinCatSession FakeWithDevice(string ethernet = "<Ethernet DeviceDesc=\"EL6631-0010 PROFINET IO Device\"/>")
    {
        var fake = OpenFake();
        fake.TreeChildren["TIID"].Add(new TreeChildInfo("PN Device", 2));
        fake.TreeChildren["TIID"].Add(new TreeChildInfo("Last Device", 2));
        fake.TreeItemXml["TIID^Last Device"] = DeviceXml("TIID^Last Device", subType: 111);
        fake.TreeItemXml[Device] = DeviceXml(Device, ecatId: 4, ecatName: El6631);
        fake.TreeChildren[Device] = [new TreeChildInfo("Inputs", 8), new TreeChildInfo("Box 1", 5)];
        fake.TreeItemXml[Box] = $"<TreeItem><ItemName>Box 1</ItemName><Comment><![CDATA[GSDML Name: {GsdmlName}\r\nPath: $(TWINCAT3DIR)Config\\Io\\Profinet\\\r\n]]></Comment><PathName>{Box}</PathName></TreeItem>";
        fake.TreeChildren[Box] = [new TreeChildInfo("Inputs", 8), new TreeChildInfo("API", 44)];
        fake.TreeChildren[Api] = [new TreeChildInfo("Term 1 (DAP Module)", 45), new TreeChildInfo("Term 2 (1 Byte Input)", 45)];
        fake.TreeItemXml[$"{Api}^Term 1 (DAP Module)"] =
            $"<TreeItem><ItemName>Term 1 (DAP Module)</ItemName><PathName>{Api}^Term 1 (DAP Module)</PathName><ItemType>45</ItemType>" +
            "<CPnIoModuleDef><ModuleIdentNr>#x00000002</ModuleIdentNr><SlotNr>#x00000000</SlotNr></CPnIoModuleDef></TreeItem>";
        fake.TreeItemXml[$"{Api}^Term 2 (1 Byte Input)"] =
            $"<TreeItem><ItemName>Term 2 (1 Byte Input)</ItemName><PathName>{Api}^Term 2 (1 Byte Input)</PathName><ItemType>45</ItemType>" +
            "<CPnIoModuleDef><ModuleName>Term 2</ModuleName><ModuleIdentNr>#x00000100</ModuleIdentNr><SlotNr>#x00000001</SlotNr></CPnIoModuleDef></TreeItem>";
        fake.ExportXml[Device] = ExportXml(2, ethernet);
        return fake;
    }

    private ProfinetCoordinator Coordinator(FakeTwinCatSession fake) => new(fake, _dir);

    private static bool Changed(FakeTwinCatSession fake) =>
        fake.CallLog.Any(c => c == "LogoutPlc()" || c.StartsWith("Create", StringComparison.Ordinal)
                              || c.StartsWith("ConsumeTreeItemXml", StringComparison.Ordinal) || c.StartsWith("Delete", StringComparison.Ordinal)
                              || c.StartsWith("Import", StringComparison.Ordinal) || c.StartsWith("Rename", StringComparison.Ordinal) || c == "SaveAll()");

    // ---- GSDML -------------------------------------------------------------------------------

    [Fact]
    public void Gsdml_ReadsDapsModulesIndexesAndSizes()
    {
        var catalog = GsdmlCatalog.Parse(Gsdml, GsdmlName);

        catalog.Daps.Select(d => (d.Number, d.IdentNumber, d.Name, d.MaxSlot)).Should().Equal((1, 1u, "EL6631-0010 V1", 63), (2, 2u, "EL6631-0010 V2", 255));
        catalog.Dap(null)!.Number.Should().Be(2);
        catalog.Modules.Select(m => (m.Index, m.Id)).Should().Equal((1, "BIT_I8"), (2, "BYTE_I4"), (3, "BYTE_O4"), (4, "MOD_X"));
        var i4 = catalog.Modules[1];
        (i4.Name, i4.IdentNumber, i4.InputBytes, i4.OutputBytes, i4.Category).Should().Be(("4 Byte Input", 0x706u, 4, 0, "Byte Module"));
        catalog.Modules[2].OutputBytes.Should().Be(4);
        catalog.Modules[3].Modular.Should().BeTrue();
        catalog.ModulesFor(catalog.Daps[0]).Select(m => m.Id).Should().Equal("BIT_I8", "BYTE_I4");
    }

    [Theory]
    [InlineData("BYTE_I4")]
    [InlineData("byte_i4")]
    [InlineData("0x706")]
    [InlineData("1798")]
    [InlineData("4 Byte Input")]
    [InlineData("4 byte eingang")]
    public void Gsdml_FindsAModuleByIdIdentOrNameInAnyLanguage(string key) =>
        GsdmlCatalog.Parse(Gsdml, GsdmlName).Find(key)!.Id.Should().Be("BYTE_I4");

    [Fact]
    public void Gsdml_NewestEl6631FileIsLast()
    {
        File.WriteAllText(Path.Combine(_dir, "GSDML-V2.33-beckhoff-EL6631-20200116.xml"), Gsdml);
        File.WriteAllText(Path.Combine(_dir, "GSDML-V2.44-beckhoff-EL6633-20251212.xml"), Gsdml);

        GsdmlCatalog.FindEl6631Files(_dir).Select(Path.GetFileName).Should().Equal("GSDML-V2.33-beckhoff-EL6631-20200116.xml", GsdmlName);
    }

    // ---- device / module XML -----------------------------------------------------------------

    [Fact]
    public void ProfinetXml_ReadsTheEtherCatBoxDeviceAndModule()
    {
        ProfinetXml.ReadEtherCatBox(BoxXml()).Should().Be((4, 2u, 0x19E73052u, "EL6631-0010-0018"));
        ProfinetXml.ReadDevice(DeviceXml(Device, ecatId: 4, ecatName: El6631)).Should().Be((118, 4, El6631));
        ProfinetXml.BindXml(4).Should().Be("<TreeItem><DeviceDef><AddressInfo><Ecat><EtherCATDeviceId>4</EtherCATDeviceId></Ecat></AddressInfo></DeviceDef></TreeItem>");
        ProfinetXml.ReadDapIdent(ExportXml(7)).Should().Be(7u);
        var m = ProfinetXml.ReadModule(
            "<TreeItem><ItemName>Term 3</ItemName><PathName>X^Term 3</PathName><CPnIoModuleDef><ModuleIdentNr>#x00000706</ModuleIdentNr><SlotNr>#x0000000A</SlotNr></CPnIoModuleDef></TreeItem>", "X");
        (m.Slot, m.IdentNumber, m.Path).Should().Be((10, 0x706u, "X^Term 3"));
    }

    [Fact]
    public void SyncTaskXml_ReadsAndPatchesOnlyTheDeviceEthernet()
    {
        string export = ExportXml();
        SyncTaskXml.Read(export).Should().Be(new SyncTaskXml.State(true, false, null));

        string patched = SyncTaskXml.Patch(export, 301);
        SyncTaskXml.Read(patched).Should().Be(new SyncTaskXml.State(true, true, 301));
        patched.Should().Contain("<Ethernet DeviceDesc=\"inside the box, not the device\" />");
        patched.Should().StartWith("<?xml version=\"1.0\"?>");

        SyncTaskXml.Read(SyncTaskXml.Patch(patched, null)).Should().Be(new SyncTaskXml.State(true, false, null));
    }

    [Fact]
    public void SyncTaskXml_DeviceWithoutEthernet_IsNotSupported() =>
        SyncTaskXml.Read("<TcSmItem><Device DevType=\"111\"><EtherCAT/></Device></TcSmItem>").Supported.Should().BeFalse();

    // ---- device add --------------------------------------------------------------------------

    [Fact]
    public void AddDevice_CreatesBindsAddsTheBoxAndSaves()
    {
        var fake = OpenFake();
        fake.NewChildName = (parent, name, subType) => subType == ProfinetXml.BoxSubType ? "boxxx1" : name; // the XAE names the box
        fake.CreatedChildXml = (parent, name, subType, vInfo, path) => subType == ProfinetXml.DeviceSubType ? DeviceXml(path) : $"<TreeItem><PathName>{path}</PathName></TreeItem>";
        fake.ConsumeXmlApplier = (path, current, payload) => DeviceXml(path, ecatId: 4, ecatName: El6631);

        string result = Coordinator(fake).AddDevice("PN Device", El6631, null, null);

        result.Should().StartWith($"Created Profinet device (RT) '{Device}' on the EL6631-0010 '{El6631}' (read-back).");
        result.Should().Contain($"Profinet box '{Device}^boxxx1' from {GsdmlName}, DAP 2 'EL6631-0010 V2'.").And.Contain("Project saved.");
        fake.CallLog.Should().ContainInOrder(
            "LogoutPlc()",
            "CreateTreeChild(TIID,PN Device,118,)",
            $"ConsumeTreeItemXml({Device})",
            $"CreateTreeChild({Device},Box 1,9127,{Path.Combine(_dir, GsdmlName)}#0x2)",
            "SaveAll()");
        fake.ConsumedXml.Single().Xml.Should().Contain("<EtherCATDeviceId>4</EtherCATDeviceId>");
    }

    [Fact]
    public void AddDevice_NotAnEl6631_IsRefused()
    {
        var fake = OpenFake();
        fake.TreeItemXml[El6631] = BoxXml(product: 0x0FA83052);

        Coordinator(fake).AddDevice("PN Device", El6631, null, null).Should().StartWith($"[Error] '{El6631}' is not an EL6631-0010");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void AddDevice_BoxAlreadyUsed_OrNameTaken_IsRefused()
    {
        var fake = FakeWithDevice();

        Coordinator(fake).AddDevice("PN 2", El6631, null, null).Should().StartWith($"[Error] The EL6631-0010 '{El6631}' is already used by the I/O device '{Device}'");
        Coordinator(fake).AddDevice("pn device", El6631, null, null).Should().StartWith("[Error] An I/O device 'PN Device' already exists");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void AddDevice_UnknownDap_ListsTheDaps()
    {
        var fake = OpenFake();
        Coordinator(fake).AddDevice("PN Device", El6631, 9, null).Should().StartWith("[Error] dap 9 does not exist").And.Contain("2 = EL6631-0010 V2");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void AddDevice_SyncTaskWithoutImage_IsRefusedBeforeTheLogout()
    {
        var fake = OpenFake();
        Coordinator(fake).AddDevice("PN Device", El6631, null, "PlcTask").Should().StartWith("[Error] Task 'TIRT^PlcTask' has no process image");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void AddDevice_BindingNotTaken_NamesTheDevice()
    {
        var fake = OpenFake();
        fake.CreatedChildXml = (parent, name, subType, vInfo, path) => DeviceXml(path);

        string result = Coordinator(fake).AddDevice("PN Device", El6631, null, null);

        result.Should().StartWith($"[Error] binding '{Device}' to '{El6631}' failed: the read-back shows '<none>'")
            .And.Contain($"Already created: I/O device '{Device}'");
        fake.CallLog.Should().NotContain("SaveAll()");
    }

    // ---- modules -----------------------------------------------------------------------------

    [Fact]
    public void ListModules_ShowsPlacedModulesAndTheDapCatalog()
    {
        var fake = FakeWithDevice();

        string result = Coordinator(fake).ListModules(Device, null);

        result.Should().StartWith($"Profinet device '{Device}': DAP 2 'EL6631-0010 V2' ({GsdmlName}) in slot 0, 1 module(s) placed, 4 module type(s) offered.");
        using var doc = JsonDocument.Parse(result[result.IndexOf('{')..]);
        doc.RootElement.GetProperty("placed")[0].GetProperty("module").GetString().Should().Be("DAP 2");
        var placed = doc.RootElement.GetProperty("placed")[1];
        placed.GetProperty("slot").GetInt32().Should().Be(1);
        placed.GetProperty("module").GetString().Should().Be("BIT_I8");
        doc.RootElement.GetProperty("catalog").EnumerateArray().Select(m => m.GetProperty("module").GetString()).Should().Equal("BIT_I8", "BYTE_I4", "BYTE_O4", "MOD_X");
        Coordinator(fake).ListModules(Device, "output").Should().Contain("1 module type(s) offered matching 'output'");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void ListModules_NotAProfinetDevice_IsAnError()
    {
        var fake = FakeWithDevice();
        Coordinator(fake).ListModules("TIID^Device 1 (EtherCAT)", null).Should().StartWith("[Error]").And.Contain("is not a Profinet device (RT)");
    }

    [Fact]
    public void AddModule_PlacesByCatalogIndexAndSlot_ReadsBack_AndSaves()
    {
        var fake = FakeWithDevice();
        fake.CreatedChildXml = (parent, name, subType, vInfo, path) =>
            $"<TreeItem><ItemName>Term 3 (4 Byte Input)</ItemName><PathName>{parent}^Term 3 (4 Byte Input)</PathName><CPnIoModuleDef><ModuleIdentNr>#x00000706</ModuleIdentNr><SlotNr>#x{int.Parse(vInfo!, System.Globalization.CultureInfo.InvariantCulture):X8}</SlotNr></CPnIoModuleDef></TreeItem>";

        string result = Coordinator(fake).AddModule(Device, "4 Byte Eingang", 2);

        fake.CallLog.Should().ContainInOrder("LogoutPlc()", $"CreateTreeChild({Api},,2,2)", "SaveAll()");
        result.Should().StartWith("Placed module 'BYTE_I4' (4 Byte Input, 4 byte(s) in, 0 byte(s) out) in slot 2:");
    }

    [Theory]
    [InlineData("BYTE_I4", 0, "slot 0 is not allowed")]
    [InlineData("BYTE_I4", 1, "slot 1 already holds 'Term 2 (1 Byte Input)'")]
    [InlineData("BYTE_I4", 256, "beyond the last slot 255")]
    [InlineData("NOPE", 2, "has no module 'NOPE'")]
    [InlineData("MOD_X", 2, "is a modular module")]
    public void AddModule_Refusals_ChangeNothing(string module, int slot, string expected)
    {
        var fake = FakeWithDevice();
        Coordinator(fake).AddModule(Device, module, slot).Should().StartWith("[Error]").And.Contain(expected);
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void AddModule_NotAllowedByTheDap_IsRefused()
    {
        var fake = FakeWithDevice();
        fake.ExportXml[Device] = ExportXml(dapIdent: 1);
        Coordinator(fake).AddModule(Device, "BYTE_O4", 2).Should().StartWith("[Error] Module 'BYTE_O4' (4 Byte Output) is not allowed with DAP 1");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void AddModule_WrongReadBack_IsAnErrorWithoutSaving()
    {
        var fake = FakeWithDevice();
        fake.CreatedChildXml = (parent, name, subType, vInfo, path) =>
            $"<TreeItem><PathName>{path}</PathName><CPnIoModuleDef><ModuleIdentNr>#x00000706</ModuleIdentNr><SlotNr>#x00000009</SlotNr></CPnIoModuleDef></TreeItem>";

        Coordinator(fake).AddModule(Device, "BYTE_I4", 2).Should().StartWith("[Error] The XAE placed").And.Contain("in slot 9");
        fake.CallLog.Should().NotContain("SaveAll()");
    }

    // ---- sync task ---------------------------------------------------------------------------

    [Fact]
    public void SyncTask_Read_Default()
    {
        var fake = FakeWithDevice();
        Coordinator(fake).SyncTask(Device, null).Should().StartWith($"Sync task of '{Device}': default (via mapping)");
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void SyncTask_Read_PortWithoutTask_Warns()
    {
        var fake = FakeWithDevice("<Ethernet DeviceDesc=\"x\" SpecialTask=\"true\" TaskPort=\"302\"/>");
        Coordinator(fake).SyncTask(Device, null).Should().Contain("AMS port 302").And.Contain("[Warn] No real-time task has AMS port 302");
    }

    [Fact]
    public void SyncTask_Set_ReimportsThePatchedExportAtTheSamePlace_AndSaves()
    {
        var fake = FakeWithDevice();

        string result = Coordinator(fake).SyncTask(Device, "IoTask");

        result.Should().StartWith($"Changed the sync task (read-back): Sync task of '{Device}': 'TIRT^IoTask' (AMS port 301).");
        fake.CallLog.Should().ContainInOrder("LogoutPlc()", "DeleteTreeChild(TIID,PN Device)", "ImportTreeChild(TIID,patched.xti,Last Device,True,PN Device)", "SaveAll()");
        var import = fake.Imports.Single();
        SyncTaskXml.Read(import.Content).Should().Be(new SyncTaskXml.State(true, true, 301));
        fake.TreeChildren["TIID"].Select(c => c.Name).Should().Equal("Device 1 (EtherCAT)", "PN Device", "Last Device");
    }

    [Fact]
    public void SyncTask_Default_RemovesTheSpecialTask()
    {
        var fake = FakeWithDevice("<Ethernet DeviceDesc=\"x\" SpecialTask=\"true\" TaskPort=\"301\"/>");

        Coordinator(fake).SyncTask(Device, "default").Should().Contain("default (via mapping)");
        SyncTaskXml.Read(fake.Imports.Single().Content).Special.Should().BeFalse();
    }

    [Fact]
    public void SyncTask_SameAsBefore_ChangesNothing()
    {
        var fake = FakeWithDevice("<Ethernet DeviceDesc=\"x\" SpecialTask=\"true\" TaskPort=\"301\"/>");
        Coordinator(fake).SyncTask(Device, "TIRT^IoTask").Should().StartWith("No change:");
        Changed(fake).Should().BeFalse();
    }

    [Theory]
    [InlineData("PlcTask", "has no process image")]
    [InlineData("Nope", "No real-time task named 'Nope'")]
    public void SyncTask_BadTask_IsRefused(string task, string expected)
    {
        var fake = FakeWithDevice();
        Coordinator(fake).SyncTask(Device, task).Should().StartWith("[Error]").And.Contain(expected);
        Changed(fake).Should().BeFalse();
    }

    [Fact]
    public void SyncTask_EtherCatMaster_IsNotSupported()
    {
        var fake = FakeWithDevice();
        fake.ExportXml["TIID^Device 1 (EtherCAT)"] = "<TcSmItem><Device DevType=\"111\"><EtherCAT/></Device></TcSmItem>";

        Coordinator(fake).SyncTask("TIID^Device 1 (EtherCAT)", "IoTask").Should().StartWith("[Error] 'TIID^Device 1 (EtherCAT)' has no sync task setting");
        Changed(fake).Should().BeFalse();
    }

    [Theory]
    [InlineData("PN Device")]
    [InlineData("TIID^PN Device^Box 1")]
    public void SyncTask_NotADevicePath_IsRefused(string device) =>
        Coordinator(OpenFake()).SyncTask(device, null).Should().StartWith("[Error]").And.Contain("is not an I/O device path");

    [Fact]
    public void SyncTask_FailedImport_PutsTheOriginalBack()
    {
        var fake = FakeWithDevice();
        fake.ImportTreeChildThrows = new InvalidOperationException("import failed");
        fake.ImportThrowsOnce = true;

        string result = Coordinator(fake).SyncTask(Device, "IoTask");

        result.Should().StartWith($"[Error] Importing '{Device}' with the new sync task failed").And.Contain("The original device was imported again.");
        SyncTaskXml.Read(fake.Imports.Single().Content).Special.Should().BeFalse();
        fake.CallLog.Should().NotContain("SaveAll()");
    }
}
