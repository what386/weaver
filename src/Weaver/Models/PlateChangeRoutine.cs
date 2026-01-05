namespace Weaver.Models;

public record PlateChangeRoutine(
    string Name,
    string Description,
    PrinterModel Model,
    GCodeRoutine GCode
);

public static partial class PlateChangeRoutines
{
    public static GCodeRoutine A1M_SwapMod =>
        new GCodeRoutine(_a1mSwapMod.GCode.Lines);

    public static GCodeRoutine A1M_APC =>
        new GCodeRoutine(_a1mAPC.GCode.Lines);
}

public static partial class PlateChangeRoutines
{
    private static readonly PlateChangeRoutine _a1mSwapMod = new(
        Name: "A1 Mini - SwapMod",
        Description: "Plate change routine for the 'SwapMod' by SwapSystems.",
        Model: PrinterModel.A1M,
        GCode: new GCodeRoutine(new[]
        {
            "G0 X-10 F5000;  park extruder",
            "G0 Z175; move Z to the top",
            "G0 Y182 F10000; move plate to ejecting position",
            "G0 Z180; prepare the lift",
            "G4 P1000; wait",
            "G0 Z186 ; trigger lift",
            "G0 Y120 F500; lift the plate",
            "G0 Y-4 Z175 F5000; slide previous plate and hook new plate",
            "G0 Y145; pull and fix the new plate",
            "G0 Y115 F1000; jump over the hook",
            "G0 Y25 F500; slide down previous plate",
            "G0 Y85 F1000; gently push the old plate",
            "G0 Y180 F5000; pull the new plate",
            "G4 P500; wait",
            "G0 Y186.5 F200;  fix the new plate and release previous plate",
            "G4 P500; wait",
            "G0 Y3 F15000; prepare new plate to be snapped to the heatbed",
            "G0 Y-5 F200; snap the new plate on the front side",
            "G4 P500; wait",
            "G0 Y10 F1000; snap the new plate on the back side",
            "G0 Y20 F15000;",
            "G0 Z150 ;",
            "G4 P1000; wait",
        })
    );

    private static readonly PlateChangeRoutine _a1mAPC = new(
        Name: "A1 Mini - AutoPlateChanger",
        Description: "Plate change routine for the open-source 'AutoPlateChanger'.",
        Model: PrinterModel.A1M,
        GCode: new GCodeRoutine(new[]
        {
            "G1 Z180 F3000",
            "G1 Y186 F6000",
            "G1 Z185 F3000",
            "G1 Y-4  F6000",
            "G1 Y186 F6000",
            "G1 Y-4  F6000",
            "G1 Y2.5 F6000",
            "G1 Y-4  F6000",
        })
    );
}
