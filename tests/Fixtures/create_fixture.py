"""Re-creates the neutral live-suite fixture tests/Fixtures/PtcXaeMcpFixture.

Drives the ONE running TwinCAT XAE (TcXaeShell, 4026) in two ways:
  * the XAE COM Automation Interface (pywin32) for what the MCP server cannot do:
    create the solution + TwinCAT project and add the PLC project;
  * the repo's own MCP server on http://localhost:5010 for everything else
    (POUs/DUTs/GVLs/interface, disabled EtherCAT master + EK1100, NC task, save).

Requirements: Windows, TwinCAT XAE 4026 running, ptc-xae-mcp.exe --http-server
listening on port 5010, Python 3.11+ with `pywin32` and `mcp` installed.

WARNING: closes the solution currently open in the XAE WITHOUT saving.

Usage:
  python tests/Fixtures/create_fixture.py [--out <dir>] [--force]

Only sources are committed (see PtcXaeMcpFixture/.gitignore). The integration
suite never opens this copy directly: it copies it to %TEMP% per run
(src/PtcXaeMcp.IntegrationTests/FixtureLayout.cs). Keep the names below in sync
with FixtureLayout.cs.
"""
import argparse
import asyncio
import os
import shutil
import sys
import time

import pythoncom
import win32com.client

SERVER = "http://localhost:5010/"
NAME = "PtcXaeMcpFixture"
PLC = "FixturePlc"
TEMPLATE = r"C:\Program Files (x86)\Beckhoff\TwinCAT\3.1\Components\Base\PrjTemplate\TwinCAT Project.tsproj"
PLC_TEMPLATE = "Standard PLC Template.plcproj"
ETHERCAT_MASTER = "Device 1 (EtherCAT)"

# ── ST sources (neutral names only) ─────────────────────────────────────────

GVL = """{attribute 'qualified_only'}
VAR_GLOBAL
	bEnable : BOOL;
	bInputSignal AT %I* : BOOL;
	nCount : UINT := 2;
	nCycleCount : UDINT;
	fValue : LREAL := 1.5;
	sText : STRING := 'fixture';
	aNames : ARRAY[0..3] OF STRING(20) := ['alpha', 'beta', 'gamma', 'delta'];
END_VAR
"""

# IbnLive probe (issue #200): the test-PRG contract the twincat_ibn_* tools talk to
# (IbnLiveCoordinator). The fixture has no test PRG: MAIN only reads these symbols,
# so a written command bit moves nothing.
GVL_IBN_PROBE = """{attribute 'qualified_only'}
VAR_GLOBAL
	bEnable : BOOL; // heartbeat: TRUE while armed, FALSE on disarm/abort/release_all
	bStepReq : BOOL; // the single command bit, written only by a human-confirmed proposal
	bAbort : BOOL; // panic/home: TRUE on disarm/abort/release_all
	bChainRunning : BOOL; // status read by twincat_list_active_chains
	nActiveChain : INT;
	nCurrentStep : INT;
	bFault : BOOL;
END_VAR
"""

# plc-instance-settings: init symbols (values live on the PLC instance, not in the source) and a
# retain variable (a RetainSrc data area). Read by MAIN so they stay in the process image.
GVL_INSTANCE_SETTINGS = """{attribute 'qualified_only'}
VAR_GLOBAL
	{attribute 'TcInitSymbol'}
	nInitParam : DINT := 10;
	{attribute 'TcInitSymbol'}
	fInitGain : LREAL := 1.5;
END_VAR
VAR_GLOBAL RETAIN
	nRetainCounter : UDINT;
END_VAR
"""

DUT_ENUM = """{attribute 'qualified_only'}
{attribute 'strict'}
TYPE E_FixtureMode :
(
	Idle := 0,
	Running := 1,
	Done := 2
) INT;
END_TYPE
"""

DUT_STRUCT = """TYPE ST_FixtureRecord :
STRUCT
	nId : UDINT;
	fValue : LREAL;
	sLabel : STRING(40);
	eMode : E_FixtureMode;
END_STRUCT
END_TYPE
"""

DUT_ALIAS = "TYPE T_FixtureName : STRING(40); END_TYPE"

# persistent-snapshot: remanent data of every kind the snapshot carries. PERSISTENT struct (nested struct,
# ENUM, STRING, WSTRING, TIME, DT, array with lower bound 1), a PERSISTENT array of structs, and an FB with a
# VAR PERSISTENT and a VAR RETAIN local, instanced twice (once inside an array). MAIN reads them so they
# stay in the symbol table.
DUT_PERSISTENT_INNER = """TYPE ST_PersistentInner :
STRUCT
	nCount : INT;
	fOffset : LREAL;
END_STRUCT
END_TYPE
"""

DUT_PERSISTENT_RECIPE = """TYPE ST_PersistentRecipe :
STRUCT
	stInner : ST_PersistentInner;
	eMode : E_FixtureMode;
	sName : STRING(20);
	wsName : WSTRING(20);
	tDelay : TIME := T#1S500MS;
	dtStamp : DT;
	aPos : ARRAY[1..5] OF LREAL;
END_STRUCT
END_TYPE
"""

FB_PERSISTENT_CELL_DECL = """FUNCTION_BLOCK FB_PersistentCell
VAR PERSISTENT
	nPersistentCount : DINT;
END_VAR
VAR RETAIN
	nRetainCount : DINT;
END_VAR
VAR
	nTransient : DINT;
END_VAR
"""

FB_PERSISTENT_CELL_IMPL = "nTransient := nPersistentCount + nRetainCount;\n"

GVL_PERSISTENT = """{attribute 'qualified_only'}
VAR_GLOBAL PERSISTENT
	stRecipe : ST_PersistentRecipe;
	arCal : ARRAY[1..3] OF ST_PersistentInner;
END_VAR
VAR_GLOBAL
	fbCell : FB_PersistentCell;
	aCells : ARRAY[1..2] OF FB_PersistentCell;
END_VAR
"""

FB_DECL = """FUNCTION_BLOCK FB_StepChain
VAR_INPUT
	bStart : BOOL;
END_VAR
VAR
	Step : INT;
	tonWait : TON;
	nRuns : UDINT;
END_VAR
"""

# FB_Base-style step chain: CASE Step OF with numeric labels, a commented-out
# step, a comma list and an ELSE branch (what twincat_locate_step expects).
FB_IMPL = """CASE Step OF
	0: // idle
		IF bStart THEN
			Step := 10;
		END_IF

	10: // wait a moment
		tonWait(IN := TRUE, PT := T#100MS);
		IF tonWait.Q THEN
			tonWait(IN := FALSE);
			Step := 1010;
		END_IF

	// 20: commented-out step, must never match

	1010: // work
		nRuns := nRuns + 1;
		Step := 1020;

	1020, 1030: // finish
		Step := 0;

ELSE
	Step := 0;
END_CASE
"""

MAIN_DECL = """PROGRAM MAIN
VAR
	fbStepChain : FB_StepChain;
	stRecord : ST_FixtureRecord;
	eMode : E_FixtureMode;
	sName : T_FixtureName := 'fixture';
	bInput : BOOL;
	sFirstName : STRING(20);
	bIbnProbeIdle : BOOL;
	fInstanceProbe : LREAL;
	stRecipeProbe : ST_PersistentRecipe;
	arCalProbe : ARRAY[1..3] OF ST_PersistentInner;
END_VAR
"""

# Every GVL symbol is referenced so none is stripped from the ADS symbol table.
MAIN_IMPL = """fbStepChain(bStart := GVL_Fixture.bEnable);
GVL_Fixture.nCycleCount := GVL_Fixture.nCycleCount + 1;
GVL_Fixture.fValue := GVL_Fixture.fValue + 0.001;
bInput := GVL_Fixture.bInputSignal;
sFirstName := GVL_Fixture.aNames[0];
stRecord.nId := GVL_Fixture.nCount;
stRecord.fValue := GVL_Fixture.fValue;
stRecord.sLabel := GVL_Fixture.sText;
stRecord.eMode := eMode;

// IbnLive probe: only read here, so every GVL_IBN_Probe symbol stays in the ADS symbol table.
bIbnProbeIdle := NOT (GVL_IBN_Probe.bEnable OR GVL_IBN_Probe.bStepReq OR GVL_IBN_Probe.bAbort
	OR GVL_IBN_Probe.bChainRunning OR GVL_IBN_Probe.bFault)
	AND GVL_IBN_Probe.nActiveChain = 0 AND GVL_IBN_Probe.nCurrentStep = 0;

// plc-instance-settings: only read here, so the init symbols and the retain variable stay in the image.
fInstanceProbe := GVL_InstanceSettings.fInitGain * DINT_TO_LREAL(GVL_InstanceSettings.nInitParam)
	+ UDINT_TO_LREAL(GVL_InstanceSettings.nRetainCounter);

// persistent-snapshot: the remanent symbols are read here so they stay in the image and the ADS symbol table.
GVL_Persistent.fbCell();
GVL_Persistent.aCells[1]();
GVL_Persistent.aCells[2]();
stRecipeProbe := GVL_Persistent.stRecipe;
arCalProbe := GVL_Persistent.arCal;

PRG_IntegrationScratch(); // [INTEGRATION-TEST-HOOK] do not remove manually
"""

SCRATCH_DECL = "PROGRAM PRG_IntegrationScratch\nVAR\nEND_VAR"
SCRATCH_IMPL = ("; // integration-test scratch POU. Body is freely overwritten and "
                "restored by the live test suite. Never put real logic here.")


# ── COM helpers ─────────────────────────────────────────────────────────────

def get_dte():
    rot = pythoncom.GetRunningObjectTable()
    ctx = pythoncom.CreateBindCtx(0)
    for moniker in rot.EnumRunning():
        if moniker.GetDisplayName(ctx, None).startswith("!TcXaeShell.DTE"):
            obj = rot.GetObject(moniker)
            return win32com.client.Dispatch(obj.QueryInterface(pythoncom.IID_IDispatch))
    raise SystemExit("No running TcXaeShell DTE in the ROT - start the XAE first.")


def com_retry(fn, attempts=60):
    """XAE rejects calls while busy (RPC_E_CALL_REJECTED / RETRYLATER) - retry."""
    for i in range(attempts):
        try:
            return fn()
        except pythoncom.com_error as ex:
            if ex.hresult in (-2147418111, -2147417846) and i < attempts - 1:
                time.sleep(1)
                continue
            raise


# ── MCP helpers ─────────────────────────────────────────────────────────────

async def with_mcp(steps):
    from mcp import ClientSession
    from mcp.client.streamable_http import streamablehttp_client

    async with streamablehttp_client(SERVER, timeout=600, sse_read_timeout=600) as (r, w, _):
        async with ClientSession(r, w) as s:
            await s.initialize()

            async def call(tool, allow_error=False, **args):
                for attempt in range(6):
                    res = await s.call_tool(tool, args)
                    text = "\n".join(c.text for c in res.content if getattr(c, "type", "") == "text")
                    failed = bool(res.isError) or text.startswith("[Error]")
                    # Freshly created nodes are sometimes briefly "not found" (XAE busy).
                    if failed and ("not found" in text or "0x8001010A" in text) and attempt < 5:
                        await asyncio.sleep(2)
                        continue
                    print(f"  {tool}: {text.splitlines()[0] if text else ''}")
                    if failed and not allow_error:
                        raise SystemExit(f"{tool} failed: {text}")
                    return text
            await steps(call)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", default=os.path.join(os.path.dirname(os.path.abspath(__file__)), NAME))
    ap.add_argument("--force", action="store_true", help="delete --out first")
    a = ap.parse_args()
    out = os.path.abspath(a.out)
    if os.path.exists(out):
        if not a.force:
            raise SystemExit(f"{out} exists - pass --force to recreate it.")
        keep = os.path.join(out, ".gitignore")
        gitignore = open(keep, encoding="utf-8").read() if os.path.exists(keep) else None
        shutil.rmtree(out)
        if gitignore is not None:
            os.makedirs(out)
            with open(keep, "w", encoding="utf-8") as f:
                f.write(gitignore)
    sln = os.path.join(out, NAME + ".sln")

    # 1. Close whatever is open (discard) and create solution + project + PLC via COM.
    async def close(call):
        # "[Error] No solution is currently open" is fine here.
        await call("twincat_close_project", allow_error=True, saveFirst=False)
    asyncio.run(with_mcp(close))

    pythoncom.CoInitialize()
    dte = get_dte()
    sol = dte.Solution
    os.makedirs(out, exist_ok=True)
    com_retry(lambda: sol.Create(out, NAME))
    proj = com_retry(lambda: sol.AddFromTemplate(TEMPLATE, os.path.join(out, NAME), NAME))
    sysman = com_retry(lambda: proj.Object)
    tipc = com_retry(lambda: sysman.LookupTreeItem("TIPC"))
    com_retry(lambda: tipc.CreateChild(PLC, 0, "", PLC_TEMPLATE))
    com_retry(lambda: sol.SaveAs(sln))
    com_retry(lambda: dte.ExecuteCommand("File.SaveAll"))
    root = None
    # The nested PLC project node is "<name> Project" with a localized suffix
    # ("Projekt" on a German XAE) and is not enumerable via Child(i) - probe it.
    for suffix in ("Project", "Projekt", "Projet", "Progetto", "Proyecto"):
        candidate = f"TIPC^{PLC}^{PLC} {suffix}"
        try:
            com_retry(lambda: sysman.LookupTreeItem(candidate + "^POUs"))
            root = candidate
            break
        except pythoncom.com_error:
            continue
    if root is None:
        raise SystemExit("PLC project node with a POUs folder not found.")
    print(f"PLC project root: {root}")

    # 2. Content via the repo's MCP tools.
    async def content(call):
        await call("twincat_open_project", slnPath=sln.replace("\\", "/"), closeCurrentFirst=True)
        dut, gvl, pou = f"{root}^DUTs", f"{root}^GVLs", f"{root}^POUs"
        await call("twincat_create_dut", parentPath=dut, name="E_FixtureMode", dutType="ENUM", declaration=DUT_ENUM)
        await call("twincat_create_dut", parentPath=dut, name="ST_FixtureRecord", dutType="STRUCT", declaration=DUT_STRUCT)
        await call("twincat_create_dut", parentPath=dut, name="T_FixtureName", dutType="ALIAS", declaration=DUT_ALIAS)
        await call("twincat_create_dut", parentPath=dut, name="ST_PersistentInner", dutType="STRUCT", declaration=DUT_PERSISTENT_INNER)
        await call("twincat_create_dut", parentPath=dut, name="ST_PersistentRecipe", dutType="STRUCT", declaration=DUT_PERSISTENT_RECIPE)
        await call("twincat_create_pou", parentPath=pou, name="FB_PersistentCell", pouType="FUNCTION_BLOCK")
        await call("twincat_write_pou_body", treePath=f"{pou}^FB_PersistentCell", declaration=FB_PERSISTENT_CELL_DECL, implementation=FB_PERSISTENT_CELL_IMPL)
        await call("twincat_create_gvl", parentPath=gvl, name="GVL_Fixture", declaration=GVL)
        await call("twincat_create_gvl", parentPath=gvl, name="GVL_Persistent", declaration=GVL_PERSISTENT)
        await call("twincat_create_gvl", parentPath=gvl, name="GVL_IBN_Probe", declaration=GVL_IBN_PROBE)
        await call("twincat_create_gvl", parentPath=gvl, name="GVL_InstanceSettings", declaration=GVL_INSTANCE_SETTINGS)
        await call("twincat_create_interface", parentPath=pou, name="I_FixtureDevice")
        await call("twincat_add_pou_member", pouPath=f"{pou}^I_FixtureDevice", kind="METHOD", name="Reset", returnType="BOOL")
        await call("twincat_create_pou", parentPath=pou, name="FB_StepChain", pouType="FUNCTION_BLOCK")
        await call("twincat_write_pou_body", treePath=f"{pou}^FB_StepChain", declaration=FB_DECL, implementation=FB_IMPL)
        # FB whose members live in a sub-folder (fix/pou-members-in-folders). There is no MCP
        # tool for folders, so the folder is created via COM (CreateChild nSubType 601 under a
        # FB, TC >= 4024.14); the members then go into the folder via add_pou_member.
        await call("twincat_create_pou", parentPath=pou, name="FB_FolderMembers", pouType="FUNCTION_BLOCK")
        fb_path = f"{pou}^FB_FolderMembers"
        live_sysman = com_retry(lambda: dte.Solution.Projects.Item(1).Object)
        fb_node = com_retry(lambda: live_sysman.LookupTreeItem(fb_path))
        com_retry(lambda: fb_node.CreateChild("Members", 601, "", None))
        members = f"{fb_path}^Members"
        await call("twincat_add_pou_member", pouPath=members, kind="PROPERTY", name="Enabled", returnType="BOOL")
        await call("twincat_add_pou_member", pouPath=members, kind="METHOD", name="Reset", returnType="BOOL")
        await call("twincat_create_pou", parentPath=pou, name="PRG_IntegrationScratch", pouType="PROGRAM")
        await call("twincat_write_pou_body", treePath=f"{pou}^PRG_IntegrationScratch", declaration=SCRATCH_DECL, implementation=SCRATCH_IMPL)
        await call("twincat_write_pou_body", treePath=f"{pou}^MAIN", declaration=MAIN_DECL, implementation=MAIN_IMPL)
        await call("twincat_save_project")
        # Build BEFORE adding EtherCAT: it creates the PLC process image the link
        # below needs, and a master without a task link would pop a modal
        # "needs Sync Master" message box during this build.
        await call("twincat_build_solution")
        await call("twincat_add_ethercat_device", name=ETHERCAT_MASTER)
        master = f"TIID^{ETHERCAT_MASTER}"
        coupler = f"{master}^Term 1 (EK1100)"
        await call("twincat_add_ethercat_box", parentPath=master, vInfo="EK1100", name="Term 1 (EK1100)")
        await call("twincat_add_ethercat_box", parentPath=coupler, vInfo="EL1008", name="Term 2 (EL1008)")
        await call("twincat_add_ethercat_box", parentPath=coupler, vInfo="EL3174", name="Term 3 (EL3174)")
        instance = root.rsplit("^", 1)[0] + f"^{PLC} Instance"
        await call("twincat_link_variables",
                   v1=f"{instance}^PlcTask Inputs^GVL_Fixture.bInputSignal",
                   v2=f"{coupler}^Term 2 (EL1008)^Channel 1^Input")
        # Disabled on purpose: activating the fixture must not need an EtherCAT
        # adapter (an enabled master without one fails IO init with a modal
        # "Zielsystem meldet fatalen Fehler"). Topology, CoE (offline ESI
        # dictionary), links and open_object work on a disabled device.
        await call("twincat_set_device_disabled", treePath=master, disabled=True)
        await call("twincat_create_nc_task", name="NC-Task 1")
        await call("twincat_save_project")
        await call("twincat_build_solution")
        await call("twincat_save_project")
        await call("twincat_close_project", saveFirst=True)

    asyncio.run(with_mcp(content))
    print(f"Fixture created: {sln}")


if __name__ == "__main__":
    sys.exit(main())
