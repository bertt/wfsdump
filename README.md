# wfsdump

## Usage 

![image](https://github.com/user-attachments/assets/09ebd759-519d-425c-bc20-141c2af47212)

```
Description:
  CLI tool for dump WFS data to PostGIS table

Usage:
  wfsdump <wfs> <wfsLayer> [options]

Arguments:
  <wfs>       WFS service
  <wfsLayer>  WFS layer name

Options:
  -?, -h, --help  Show help and usage information
  --version       Show version information
  --connection    Connection string [default: Host=localhost;Username=postgres;Password=postgres;Database=postgres]
  --output        output table [default: public.wfs_dump]
  --columns       output columns geometry,attributes (csv) [default: geom,attributes]
  --jobs          Number of parallel jobs [default: 2]
  --bbox          bbox (space separated) [default: -179|-85|179|85]
  --z             Tile Z [default: 14]
  --epsg          EPSG [default: 4326]
```

Create output table sample:

```
CREATE TABLE public.wfs_dump (
    geom GEOMETRY,
    attributes jsonb
);
```

## Projections

By default, EPSG code 4326 is used for requesting the WFS and storing the geometries.

If another EPSG code is specified, the EPSG code is used to request the WFS service (for the bounding box and output SRS) and store 
the geometries. 

Note: The bbox should always be defined in EPSG:4326.

## Sample

```
./wfsdump https://ahocevar.com/geoserver/wfs ne:ne_10m_admin_0_countries --z 1 --jobs 1
```

Other WFS services are not tested (yet)

## History

25-08-28: Release 0.2 alpha 16, adding EPSG output code

25-03-13: Release 0.2, adding error handling

25-03-12: Initial version 0.1
