# wfsdump

```
Description:
  CLI tool for dump WFS data to PostGIS table

Usage:
  wfsdump <wfs> <wfsLayer> [options]

Arguments:
  <wfs>       WFS service
  <wfsLayer>  WFS layer

Options:
  --connection <connection>  Connection string [default:
                             Host=localhost;Username=postgres;Password=postgres;Database=postgres]
  --output <output>          output table [default: public.wfs_dump]
  --columns <columns>        output columns geometry,attributes (csv) [default: geom,attributes]
  --jobs <jobs>              Number of parallel jobs [default: 2]
  --bbox <bbox>              bbox (space separated) [default: -179 -85 179 85]
  --z <z>                    Tile Z [default: 14]
  --version                  Show version information
  -?, -h, --help             Show help and usage information
```

Create output table sample:

```
CREATE TABLE public.wfs_dump (
    geom GEOMETRY,
    attributes jsonb
);
```


Sample:

```
./wfsdump https://ahocevar.com/geoserver/wfs ne:ne_10m_admin_0_countries --z 5 --jobs 1
```

Other WFS services are not tested (yet)
