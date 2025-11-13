using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using MaxRev.Gdal.Core;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using Newtonsoft.Json;
using Npgsql;
using OSGeo.OSR;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Tiles.Tools;

namespace wfsdump.ui;

public partial class MainWindow : Window
{
    private CancellationTokenSource? _cancellationTokenSource;
    private bool _isRunning = false;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void StartButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_isRunning)
            return;

        try
        {
            // Validate inputs
            if (string.IsNullOrWhiteSpace(WfsUrlTextBox.Text))
            {
                await ShowMessage("Error", "Please enter a WFS URL");
                return;
            }

            if (string.IsNullOrWhiteSpace(WfsLayerTextBox.Text))
            {
                await ShowMessage("Error", "Please enter a WFS Layer name");
                return;
            }

            // Parse parameters
            var wfs = WfsUrlTextBox.Text;
            var wfsLayer = WfsLayerTextBox.Text;
            var baseConnectionString = ConnectionStringTextBox.Text ?? "Host=localhost;Username=postgres;Database=postgres";
            var password = PasswordTextBox.Text ?? "";
            
            var connectionString = baseConnectionString;
            if (!string.IsNullOrWhiteSpace(password))
            {
                connectionString += $";Password={password}";
            }
            
            var outputTable = OutputTableTextBox.Text ?? "public.wfs_dump";
            var columns = ColumnsTextBox.Text ?? "geom,attributes";
            var jobs = (int)(JobsNumeric.Value ?? 2);
            var tileZ = (int)(TileZNumeric.Value ?? 14);
            var epsg = (int)(EpsgNumeric.Value ?? 4326);

            var bboxParts = (BboxTextBox.Text ?? "-179 -85 179 85").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (bboxParts.Length != 4)
            {
                await ShowMessage("Error", "Bounding box must have 4 values (minX minY maxX maxY)");
                return;
            }

            var bbox = new double[4];
            for (int i = 0; i < 4; i++)
            {
                if (!double.TryParse(bboxParts[i], out bbox[i]))
                {
                    await ShowMessage("Error", $"Invalid bounding box value: {bboxParts[i]}");
                    return;
                }
            }

            var columnParts = columns.Split(',');
            if (columnParts.Length != 2)
            {
                await ShowMessage("Error", "Columns must be in format: geometry,attributes");
                return;
            }

            // Start processing
            _isRunning = true;
            _cancellationTokenSource = new CancellationTokenSource();
            
            StartButton.IsEnabled = false;
            StopButton.IsEnabled = true;
            
            ClearLog();
            AppendLog("Starting WFS dump process...");
            UpdateStatus("Starting...");
            ProgressBar.Value = 0;

            await RunDump(wfs, wfsLayer, connectionString, outputTable, jobs, bbox, tileZ, columnParts[0], columnParts[1], epsg, _cancellationTokenSource.Token);
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            UpdateStatus("Error occurred");
        }
        finally
        {
            _isRunning = false;
            StartButton.IsEnabled = true;
            StopButton.IsEnabled = false;
        }
    }

    private void StopButton_Click(object? sender, RoutedEventArgs e)
    {
        _cancellationTokenSource?.Cancel();
        AppendLog("Cancellation requested...");
        UpdateStatus("Stopping...");
    }

    private void ClearButton_Click(object? sender, RoutedEventArgs e)
    {
        ClearLog();
        ProgressBar.Value = 0;
        UpdateStatus("Ready to start");
    }

    private async Task RunDump(string wfs, string wfsLayer, string connectionString, string outputTable, 
        int jobs, double[] bbox, int tileZ, string outputGeometryColumn, string outputAttributesColumn, 
        int epsg, CancellationToken cancellationToken)
    {
        try
        {
            AppendLog($"WFS: {wfs}");
            AppendLog($"WFS Layer: {wfsLayer}");
            AppendLog($"Connection: {MaskPassword(connectionString)}");
            AppendLog($"Output table: {outputTable}");
            AppendLog($"Jobs: {jobs}");
            AppendLog($"Extent: {bbox[0]}, {bbox[1]}, {bbox[2]}, {bbox[3]}");
            AppendLog($"Tile Z: {tileZ}");
            AppendLog($"EPSG: {epsg}");

            var tiles = Tilebelt.GetTilesOnLevel(bbox, tileZ);
            AppendLog($"Tiles count: {tiles.Count}");
            UpdateStatus($"Processing {tiles.Count} tiles...");

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = jobs,
                CancellationToken = cancellationToken
            };

            var client = new HttpClient();
            var tilesWithErrors = new List<ErrorTile>();
            int processedCount = 0;

            if (epsg != 4326)
            {
                GdalBase.ConfigureAll();
            }

            await Parallel.ForEachAsync(tiles, parallelOptions, async (tile, token) =>
            {
                try
                {
                    var tileExtent = tile.Bounds();

                    if (epsg != 4326)
                    {
                        tileExtent = Project(tileExtent, epsg);
                    }

                    var query = $"{wfs}?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetFeature&TYPENAME={wfsLayer}&OUTPUTFORMAT=application/json&BBOX={tileExtent[0]},{tileExtent[1]},{tileExtent[2]},{tileExtent[3]},EPSG:{epsg}&SRSNAME=EPSG:{epsg}";

                    var response = await client.GetAsync(query, token);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseString = await response.Content.ReadAsStringAsync();
                        var textReader = new StringReader(responseString);
                        var serializer = GeoJsonSerializer.CreateDefault();

                        var featureCollection = serializer.Deserialize<FeatureCollection>(new JsonTextReader(textReader))!;

                        var featuresInTile = new List<IFeature>();
                        foreach (var feature in featureCollection)
                        {
                            var geometry = feature.Geometry;
                            var centroid = geometry.Centroid;

                            if (tileExtent[0] < centroid.X &&
                                tileExtent[1] < centroid.Y &&
                                tileExtent[2] > centroid.X &&
                                tileExtent[3] > centroid.Y)
                            {
                                featuresInTile.Add(feature);
                            }
                        }

                        if (featuresInTile.Count > 0)
                        {
                            if (featuresInTile.Count > 5000)
                            {
                                await Dispatcher.UIThread.InvokeAsync(() =>
                                    AppendLog($"Warning: tile {tile} has {featuresInTile.Count} features"));
                            }

                            var connection = new NpgsqlConnection(connectionString);
                            connection.Open();
                            var transaction = connection.BeginTransaction();
                            var command = connection.CreateCommand();
                            command.Transaction = transaction;

                            foreach (var feature in featuresInTile)
                            {
                                var attributes = feature.Attributes;
                                var attributesJson = JsonConvert.SerializeObject(attributes);
                                string jsonEscaped = attributesJson.Replace("'", "''");
                                var geometry = feature.Geometry;

                                var wkb = new WKBWriter().Write(geometry);
                                var wkbHex = BitConverter.ToString(wkb).Replace("-", "");
                                var wkbHexLiteral = $"\\x{wkbHex}";
                                var insert = $"INSERT INTO {outputTable} ({outputGeometryColumn}, {outputAttributesColumn}) VALUES (ST_GeomFromWKB('{wkbHexLiteral}', {epsg}), '{jsonEscaped}'::jsonb)";
                                command.CommandText = insert;
                                command.ExecuteNonQuery();
                            }

                            transaction.Commit();
                            connection.Close();
                        }
                    }
                    else
                    {
                        lock (tilesWithErrors)
                        {
                            tilesWithErrors.Add(new ErrorTile { Tile = tile, StatusCode = (int)response.StatusCode });
                        }
                    }

                    Interlocked.Increment(ref processedCount);
                    var progress = (double)processedCount / tiles.Count * 100;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        ProgressBar.Value = progress;
                        UpdateStatus($"Processing: {processedCount}/{tiles.Count} tiles ({progress:F1}%)");
                    });
                }
                catch (Exception ex)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                        AppendLog($"Error processing tile: {ex.Message}"));
                }
            });

            AppendLog($"\nTiles with errors: {tilesWithErrors.Count}");

            if (tilesWithErrors.Count > 0)
            {
                var distinctStatusCodes = tilesWithErrors.Select(x => x.StatusCode).Distinct();
                var errorStatus = string.Join(",", distinctStatusCodes);
                AppendLog($"Status codes: {errorStatus}");
            }

            AppendLog("Program finished successfully!");
            UpdateStatus("Completed");
            ProgressBar.Value = 100;
        }
        catch (OperationCanceledException)
        {
            AppendLog("Operation cancelled by user");
            UpdateStatus("Cancelled");
        }
        catch (Exception ex)
        {
            AppendLog($"Error: {ex.Message}");
            AppendLog($"Stack trace: {ex.StackTrace}");
            UpdateStatus("Error occurred");
        }
    }

    private static double[] Project(double[] extent, int toEpsg)
    {
        var src = new SpatialReference("");
        src.ImportFromEPSG(4326);
        src.SetAxisMappingStrategy(AxisMappingStrategy.OAMS_TRADITIONAL_GIS_ORDER);

        var dst = new SpatialReference("");
        dst.ImportFromEPSG(toEpsg);

        var ct = new CoordinateTransformation(src, dst, new CoordinateTransformationOptions());
        double[] min = new double[] { extent[0], extent[1], 0 };
        double[] max = new double[] { extent[2], extent[3], 0 };
        ct.TransformPoint(min);
        ct.TransformPoint(max);
        return new double[] { min[0], min[1], max[0], max[1] };
    }

    private void AppendLog(string message)
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogTextBox.Text += $"[{DateTime.Now:HH:mm:ss}] {message}\n";
            if (LogTextBox.GetType().GetProperty("CaretIndex") != null)
            {
                // Scroll to end
                LogTextBox.CaretIndex = LogTextBox.Text?.Length ?? 0;
            }
        });
    }

    private void ClearLog()
    {
        Dispatcher.UIThread.Post(() =>
        {
            LogTextBox.Text = string.Empty;
        });
    }

    private void UpdateStatus(string status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StatusTextBlock.Text = status;
        });
    }

    private static string MaskPassword(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return connectionString;

        var parts = connectionString.Split(';');
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (part.StartsWith("Password=", StringComparison.OrdinalIgnoreCase))
            {
                parts[i] = "Password=***";
            }
        }
        return string.Join(";", parts);
    }

    private async Task ShowMessage(string title, string message)
    {
        var messageBox = new Window
        {
            Title = title,
            Width = 400,
            Height = 150,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 15,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                    new Button
                    {
                        Content = "OK",
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Width = 80
                    }
                }
            }
        };

        var button = ((StackPanel)messageBox.Content).Children.OfType<Button>().First();
        button.Click += (s, e) => messageBox.Close();

        await messageBox.ShowDialog(this);
    }

    private class ErrorTile
    {
        public Tile Tile { get; set; } = null!;
        public int StatusCode { get; set; }
    }
}