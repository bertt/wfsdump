using NetTopologySuite.IO;
using NetTopologySuite.Features;
using Tiles.Tools;
using Npgsql;
using Newtonsoft.Json;
using System.CommandLine;

Console.WriteLine("Dump WFS to PostGIS table console tool ");

var rootCommand = GetRootCommand();

await rootCommand.InvokeAsync(args);

async Task RunIt(string wfs, string wfsLayer, string connectionString, string outputTable, int jobs, double[] bbox, int tileZ, string outputGeometryColumn, string outputAttributesColumn)
{
    Console.WriteLine($"WFS: {wfs}");
    Console.WriteLine($"WFS Layer: {wfsLayer}");
    Console.WriteLine($"Connection: {connectionString}");
    Console.WriteLine($"Output table: {outputTable}");
    Console.WriteLine($"Jobs: {jobs}");
    Console.WriteLine($"Extent: {bbox[0]}, {bbox[1]}, {bbox[2]}, {bbox[3]}");
    Console.WriteLine($"Tile Z: {tileZ}");

    var tiles = Tilebelt.GetTilesOnLevel(bbox, tileZ);

    Console.WriteLine("Tiles count: " + tiles.Count);
    var parallelOptions = new ParallelOptions
    {

        MaxDegreeOfParallelism = jobs
    };

    var client = new HttpClient
    {
        DefaultRequestHeaders = { ConnectionClose = true }
    };

    var tilesWithErrors = new List<ErrorTile>();

    try
    {

        await Parallel.ForEachAsync(tiles, parallelOptions, async (tile, token) =>
        {
            ShowProgress(tiles.IndexOf(tile), tiles.Count);

            var tileExtent = tile.Bounds();
            var query = $"{wfs}?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature&TYPENAME={wfsLayer}&OUTPUTFORMAT=application/json&BBOX={tileExtent[0]},{tileExtent[1]},{tileExtent[2]},{tileExtent[3]},EPSG:4326";

            var response = await client.GetAsync(query);

            // check for statusCode 500
            if (!response.IsSuccessStatusCode)
            {
                string json = await response.Content.ReadAsStringAsync();
                var reader = new GeoJsonReader();
                var featureCollection = reader.Read<FeatureCollection>(json);

                var featuresInTile = new List<IFeature>();
                foreach (var feature in featureCollection)
                {
                    var geometry = feature.Geometry;
                    var centroid = geometry.Centroid;

                    var tileBounds = tile.Bounds();
                    // check if centroid is within tile
                    if (tileBounds[0] < centroid.X &&
                        tileBounds[1] < centroid.Y &&
                        tileBounds[2] > centroid.X &&
                        tileBounds[3] > centroid.Y)
                    {
                        featuresInTile.Add(feature);
                    }
                }

                if (featuresInTile.Count > 0)
                {
                    if (featuresInTile.Count > 5000)
                    {
                        Console.WriteLine($"Tile {tile} has {featuresInTile.Count} features...");
                    }
                    var connection = new NpgsqlConnection(connectionString);
                    connection.Open();
                    var transaction = connection.BeginTransaction();
                    var command = connection.CreateCommand();
                    command.Transaction = transaction;

                    // insert features
                    foreach (var feature in featuresInTile)
                    {
                        var attributes = feature.Attributes;
                        // convert attributes to json
                        var attributesJson = JsonConvert.SerializeObject(attributes);
                        string jsonEscaped = attributesJson.Replace("'", "''");
                        var geometry = feature.Geometry;

                        var wkb = new WKBWriter().Write(geometry);
                        var wkbHex = BitConverter.ToString(wkb).Replace("-", "");
                        var wkbHexLiteral = $"\\x{wkbHex}";
                        var insert = $"INSERT INTO {outputTable} ({outputGeometryColumn}, {outputAttributesColumn}) VALUES (ST_GeomFromWKB('{wkbHexLiteral}', 4326), '{jsonEscaped}'::jsonb)";
                        command.CommandText = insert;
                        command.ExecuteNonQuery();
                    }

                    transaction.Commit();
                    connection.Close();
                }
            }
            else
            {
                tilesWithErrors.Add(new ErrorTile { Tile = tile, StatusCode = (int)response.StatusCode });
            }
        });
        ShowProgress(tiles.Count, tiles.Count);

        Console.WriteLine();
        Console.WriteLine("Tiles with errors: " + tilesWithErrors.Count);

        // select distinct status codes of tilesWithErrors
        if(tilesWithErrors.Count > 0)
        {
            var distinctStatusCodes = tilesWithErrors.Select(x => x.StatusCode).Distinct();
            var errorStatus = string.Join(",", distinctStatusCodes);
            Console.WriteLine("Status codes : " + errorStatus);
        }

        Console.WriteLine("Program finished");
    }
    catch (Exception ex)
    {
        // write stack trace
        Console.WriteLine(ex);
    }
}

static void ShowProgress(int progress, int total)
{
    int width = 50; // Breedte van de progressbalk
    int progressWidth = (int)((double)progress / total * width);

    Console.Write("\r[");
    Console.Write(new string('#', progressWidth));
    Console.Write(new string('-', width - progressWidth));
    var perc = (double)progress * 100 / total;
    Console.Write($"] {perc:F2}%");
}

RootCommand GetRootCommand()
{
    var wfsArgument = new Argument<string>(
               "wfs",
               description: "WFS service"
           );

    var wfsLayerArgument = new Argument<string>(
               "wfsLayer",
               description: "WFS layer"
           );

    var connectionStringOption = new Option<string>(["--connection"], getDefaultValue: () => "Host=localhost;Username=postgres;Password=postgres;Database=postgres", "Connection string")
    {
        IsRequired = false,
        Description = "Connection string"
    };

    var ouputTableOption = new Option<string>(["--output"], getDefaultValue: () => "public.wfs_dump", "Output table")
    {
        IsRequired = false,
        Description = "output table"
    };

    var jobsOption = new Option<int>("--jobs", getDefaultValue: () => 2, "jobs")
    {
        IsRequired = false,
        Description = "Number of parallel jobs",
    };

    var bboxOption = new Option<double[]>(["--bbox"], getDefaultValue: () => [-179, -85, 179, 85], "bbox")
    {
        IsRequired = false,
        Description = "bbox (space separated)",
        AllowMultipleArgumentsPerToken = true
    };

    var tileZOption = new Option<int>(["--z"], getDefaultValue: () => 14, "Tile Z level")
    {
        IsRequired = false,
        Description = "Tile Z"
    };

    var columnsOption = new Option<string>(["--columns"], getDefaultValue: () => "geom,attributes", "Output columns")
    {
        IsRequired = false,
        Description = "output columns geometry,attributes (csv)"
    };

    var rootCommand = new RootCommand("CLI tool for dump WFS data to PostGIS table");
    rootCommand.AddArgument(wfsArgument);
    rootCommand.AddArgument(wfsLayerArgument);
    rootCommand.AddOption(connectionStringOption);
    rootCommand.AddOption(ouputTableOption);
    rootCommand.AddOption(columnsOption);
    rootCommand.AddOption(jobsOption);
    rootCommand.AddOption(bboxOption);
    rootCommand.AddOption(tileZOption);


    rootCommand.SetHandler(async (wfs, wfsLayer, connectionString, outputTable, columnsOption, jobs, bbox, tileZ) =>
    {
        var geomColumn = columnsOption.Split(",")[0];
        var attributesColumn = columnsOption.Split(",")[1];
        await RunIt(wfs, wfsLayer, connectionString, outputTable, jobs, bbox, tileZ, geomColumn, attributesColumn);

    },
    wfsArgument, wfsLayerArgument, connectionStringOption, ouputTableOption, columnsOption, jobsOption, bboxOption, tileZOption);
    return rootCommand;
}


public class ErrorTile
{
    public Tile Tile { get; set; }
    public int StatusCode { get; set; }
}