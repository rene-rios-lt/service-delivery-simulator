namespace ServiceDelivery.Simulator.Models;

public static class IowaRoutes
{
    public static IReadOnlyList<VehicleRoute> All { get; } = new List<VehicleRoute>
    {
        new()
        {
            VehicleId = "V-001",
            Waypoints = new List<RouteWaypoint>
            {
                new(41.6002, -93.7224), // Clive
                new(41.5700, -93.7111), // West Des Moines
                new(41.6266, -93.7120), // Urbandale
                new(41.6733, -93.7073), // Johnston
                new(41.7308, -93.6064), // Ankeny
                new(41.6461, -93.4710), // Altoona
            }
        },
        new()
        {
            VehicleId = "V-002",
            Waypoints = new List<RouteWaypoint>
            {
                new(42.0082, -91.6440), // Cedar Rapids
                new(42.0469, -91.6816), // Hiawatha
                new(42.0341, -91.5973), // Marion
                new(41.7497, -91.6082), // North Liberty
                new(41.6874, -91.5824), // Coralville
                new(41.6611, -91.5302), // Iowa City
            }
        },
        new()
        {
            VehicleId = "V-003",
            Waypoints = new List<RouteWaypoint>
            {
                new(42.4999, -96.4003), // Sioux City
                new(42.7952, -96.1659), // Le Mars
                new(42.6469, -95.2086), // Storm Lake
                new(43.1419, -95.1442), // Spencer
                new(43.0769, -95.6260), // Estherville
                new(42.8360, -96.0126), // Cherokee
            }
        },
        new()
        {
            VehicleId = "V-004",
            Waypoints = new List<RouteWaypoint>
            {
                new(41.5236, -90.5776), // Davenport
                new(41.5245, -90.4410), // Bettendorf
                new(41.5978, -90.3442), // Le Claire
                new(41.8294, -90.5378), // DeWitt
                new(41.8444, -90.1887), // Clinton
                new(41.4244, -91.0435), // Muscatine
            }
        },
        new()
        {
            VehicleId = "V-005",
            Waypoints = new List<RouteWaypoint>
            {
                new(42.4928, -92.3426), // Waterloo
                new(42.5244, -92.4531), // Cedar Falls
                new(42.4057, -92.4624), // Hudson
                new(42.4721, -92.2782), // Evansdale
                new(42.4744, -92.0635), // Jesup
                new(42.4652, -91.8897), // Independence
            }
        },
        new()
        {
            VehicleId = "V-006",
            Waypoints = new List<RouteWaypoint>
            {
                new(42.5006, -90.6646), // Dubuque
                new(42.4816, -91.1289), // Dyersville
                new(42.4838, -91.4560), // Manchester
                new(42.8539, -91.4054), // Elkader
                new(43.2688, -91.4741), // Waukon
                new(43.3069, -91.7882), // Decorah
            }
        },
        new()
        {
            VehicleId = "V-007",
            Waypoints = new List<RouteWaypoint>
            {
                new(41.2619, -95.8608), // Council Bluffs
                new(41.6527, -95.3272), // Harlan
                new(41.4766, -95.3366), // Avoca
                new(41.4033, -95.0139), // Atlantic
                new(41.0013, -95.2302), // Red Oak
                new(40.7651, -95.3697), // Shenandoah
            }
        },
        new()
        {
            VehicleId = "V-008",
            Waypoints = new List<RouteWaypoint>
            {
                new(43.1536, -93.2010), // Mason City
                new(43.1380, -93.3802), // Clear Lake
                new(43.2630, -93.6378), // Forest City
                new(43.1004, -93.6017), // Garner
                new(42.7405, -93.2010), // Hampton
                new(42.5218, -93.2601), // Iowa Falls
            }
        },
    };
}
