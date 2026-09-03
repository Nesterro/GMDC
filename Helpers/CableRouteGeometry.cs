using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace GMDC.Helpers
{
    /// <summary>
    /// All geometry algorithms for cable route creation.
    /// Ported from the pyRevit script.py.
    /// </summary>
    public static class CableRouteGeometry
    {
        // ─────────────────────────────────────────────────────────────
        // POINT EXTRACTION
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns the start/end point for routing.
        /// For MEP curves (cable trays, conduits, fittings):
        /// - If clicked near an open connector, snaps to the open connector.
        /// - Otherwise uses the exact clicked point (reference.GlobalPoint).
        /// For equipment (FamilyInstance):
        /// - Uses connector if available, or center of top face (X,Y center; Z = BoundingBox.Max.Z).
        /// </summary>
        public static XYZ GetTopCenterPoint(Document hostDoc, Reference reference)
        {
            try
            {
                if (reference == null) return null;

                Transform trans = Transform.Identity;
                Element el;

                if (reference.LinkedElementId != ElementId.InvalidElementId)
                {
                    var linkInst = hostDoc.GetElement(reference.ElementId) as RevitLinkInstance;
                    if (linkInst == null) return null;
                    trans = linkInst.GetTotalTransform();
                    Document linkDoc = linkInst.GetLinkDocument();
                    el = linkDoc?.GetElement(reference.LinkedElementId);
                }
                else
                {
                    el = hostDoc.GetElement(reference.ElementId);
                }

                if (el == null) return reference.GlobalPoint;

                XYZ clickPt = reference.GlobalPoint;

                // 1. Check if the element is a Cable Tray, Conduit, or Fitting
                bool isMepRun = el is Autodesk.Revit.DB.Electrical.CableTray ||
                                el is Autodesk.Revit.DB.Electrical.Conduit ||
                                (el.Category != null && (
                                    el.Category.Id.Value == (long)BuiltInCategory.OST_CableTray ||
                                    el.Category.Id.Value == (long)BuiltInCategory.OST_CableTrayFitting ||
                                    el.Category.Id.Value == (long)BuiltInCategory.OST_Conduit ||
                                    el.Category.Id.Value == (long)BuiltInCategory.OST_ConduitFitting));

                if (isMepRun)
                {
                    var conns = GetConnectors(el);
                    // Check for open connectors (not connected to other elements)
                    var openConns = conns.Where(c => !c.IsConnected).ToList();
                    if (openConns.Count > 0)
                    {
                        if (clickPt != null)
                        {
                            // If user clicked close to an open connector (within ~3.5 ft / 1 m), snap to that open connector
                            var nearestOpen = openConns.OrderBy(c => trans.OfPoint(c.Origin).DistanceTo(clickPt)).First();
                            if (trans.OfPoint(nearestOpen.Origin).DistanceTo(clickPt) < 3.5)
                            {
                                return trans.OfPoint(nearestOpen.Origin);
                            }
                        }
                        else
                        {
                            return trans.OfPoint(openConns[0].Origin);
                        }
                    }

                    if (clickPt != null)
                    {
                        return clickPt;
                    }
                }

                // 2. For FamilyInstance equipment, check if it has electrical connectors
                if (el is FamilyInstance fi)
                {
                    var conns = GetConnectors(el);
                    if (conns.Count > 0)
                    {
                        if (clickPt != null)
                        {
                            return trans.OfPoint(conns.OrderBy(c => trans.OfPoint(c.Origin).DistanceTo(clickPt)).First().Origin);
                        }
                    }
                }

                // 3. For equipment without connectors, use top center of bounding box
                BoundingBoxXYZ bb = el.get_BoundingBox(null);
                if (bb != null)
                {
                    double cx = (bb.Min.X + bb.Max.X) / 2.0;
                    double cy = (bb.Min.Y + bb.Max.Y) / 2.0;
                    double zt = bb.Max.Z;
                    XYZ local = new XYZ(cx, cy, zt);
                    return trans.OfPoint(local);
                }

                if (clickPt != null) return clickPt;

                Location loc = el.Location;
                if (loc is LocationPoint lp) return trans.OfPoint(lp.Point);
                if (loc is LocationCurve lc) return trans.OfPoint(lc.Curve.Evaluate(0.5, true));

                return null;
            }
            catch
            {
                return reference?.GlobalPoint;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // CONNECTOR HELPERS
        // ─────────────────────────────────────────────────────────────

        private static List<Connector> GetConnectors(Element el)
        {
            var result = new List<Connector>();
            try
            {
                ConnectorManager cm = null;
                if (el is FamilyInstance fi) cm = fi.MEPModel?.ConnectorManager;
                else
                {
                    var prop = el.GetType().GetProperty("ConnectorManager");
                    cm = prop?.GetValue(el) as ConnectorManager;
                }
                if (cm == null) return result;
                foreach (Connector c in cm.Connectors) result.Add(c);
            }
            catch { }
            return result;
        }

        private static XYZ GetElementCenter(Element el)
        {
            Location loc = el.Location;
            if (loc is LocationPoint lp) return lp.Point;
            if (loc is LocationCurve lc) return lc.Curve.Evaluate(0.5, true);
            BoundingBoxXYZ bb = el.get_BoundingBox(null);
            if (bb != null) return (bb.Min + bb.Max) * 0.5;
            return null;
        }

        private static XYZ GetConnectorIntersection(Connector c1, Connector c2)
        {
            if (c1 == null || c2 == null) return null;
            try
            {
                if (c1.CoordinateSystem == null || c2.CoordinateSystem == null) return null;

                XYZ p1 = c1.Origin;
                XYZ v1 = -c1.CoordinateSystem.BasisZ.Normalize();
                XYZ p2 = c2.Origin;
                XYZ v2 = -c2.CoordinateSystem.BasisZ.Normalize();

                double dot = v1.DotProduct(v2);
                if (Math.Abs(Math.Abs(dot) - 1.0) < 1e-5)
                {
                    return null;
                }

                XYZ w0 = p1 - p2;
                double a = v1.DotProduct(v1);
                double b = v1.DotProduct(v2);
                double c = v2.DotProduct(v2);
                double d = v1.DotProduct(w0);
                double e = v2.DotProduct(w0);

                double denom = a * c - b * b;
                if (Math.Abs(denom) < 1e-6) return null;

                double s = (b * e - c * d) / denom;
                double t = (a * e - b * d) / denom;

                XYZ pt1 = p1 + s * v1;
                XYZ pt2 = p2 + t * v2;

                return (pt1 + pt2) * 0.5;
            }
            catch
            {
                return null;
            }
        }

        // ─────────────────────────────────────────────────────────────
        // SORT ELEMENTS BY GEOMETRY
        // ─────────────────────────────────────────────────────────────

        public static List<Element> SortByGeometry(IList<Element> elements, XYZ startPt)
        {
            var sorted = new List<Element>();
            var pool = new List<Element>(elements);
            XYZ currentPos = startPt;

            while (pool.Count > 0)
            {
                Element best = null;
                double minDist = double.MaxValue;

                foreach (var el in pool)
                {
                    var conns = GetConnectors(el);
                    double d;
                    if (conns.Count > 0)
                        d = conns.Min(c => c.Origin.DistanceTo(currentPos));
                    else
                    {
                        var ctr = GetElementCenter(el);
                        d = ctr != null ? ctr.DistanceTo(currentPos) : double.MaxValue;
                    }

                    if (d < minDist)
                    {
                        minDist = d;
                        best = el;
                    }
                }

                if (best == null) break;
                sorted.Add(best);
                pool.Remove(best);

                var bConns = GetConnectors(best);
                if (bConns.Count > 0)
                    currentPos = bConns.OrderByDescending(c => c.Origin.DistanceTo(currentPos)).First().Origin;
                else
                {
                    var ctr = GetElementCenter(best);
                    if (ctr != null) currentPos = ctr;
                }
            }

            return sorted;
        }

        // ─────────────────────────────────────────────────────────────
        // BUILD CONTINUOUS CENTRELINE
        // ─────────────────────────────────────────────────────────────

        public static List<Curve> GetContinuousCenterline(List<Element> elements, XYZ startPt, XYZ endPt)
        {
            var rawCurves = new List<Curve>();
            XYZ lookAt = startPt;

            for (int idx = 0; idx < elements.Count; idx++)
            {
                var el = elements[idx];
                try
                {
                    Location loc = el.Location;
                    if (loc is LocationCurve lc)
                    {
                        Curve curve = lc.Curve;
                        var conns = GetConnectors(el);
                        if (curve != null && conns.Count >= 2)
                        {
                            XYZ p1 = conns[0].Origin, p2 = conns[1].Origin;
                            Line line = p1.DistanceTo(lookAt) <= p2.DistanceTo(lookAt)
                                ? Line.CreateBound(p1, p2)
                                : Line.CreateBound(p2, p1);
                            rawCurves.Add(line);
                            lookAt = line.GetEndPoint(1);
                        }
                        else if (curve != null)
                        {
                            rawCurves.Add(curve);
                            lookAt = curve.GetEndPoint(1);
                        }
                    }
                    else
                    {
                        var conns = GetConnectors(el);
                        if (conns.Count < 2) continue;
                        var pts = conns.Select(c => c.Origin).ToList();
                        XYZ inPt = pts.OrderBy(p => p.DistanceTo(lookAt)).First();
                        XYZ outTarget = endPt;
                        if (idx < elements.Count - 1)
                        {
                            var nextConns = GetConnectors(elements[idx + 1]);
                            if (nextConns.Count > 0)
                                outTarget = nextConns.OrderBy(c => c.Origin.DistanceTo(inPt)).First().Origin;
                        }
                        var remaining = pts.Where(p => p.DistanceTo(inPt) > 0.001).ToList();
                        if (remaining.Count == 0) continue;
                        XYZ outPt = remaining.OrderBy(p => p.DistanceTo(outTarget)).First();

                        // Build arc or line for fitting
                        Connector connIn = conns.FirstOrDefault(c => c.Origin.DistanceTo(inPt) < 0.001);
                        Connector connOut = conns.FirstOrDefault(c => c.Origin.DistanceTo(outPt) < 0.001);
                        XYZ interPt = GetConnectorIntersection(connIn, connOut);
                        if (interPt == null)
                        {
                            XYZ corner1 = new XYZ(inPt.X, outPt.Y, inPt.Z);
                            XYZ corner2 = new XYZ(outPt.X, inPt.Y, inPt.Z);
                            interPt = Math.Abs(corner1.DistanceTo(inPt) - corner1.DistanceTo(outPt))
                                       <= Math.Abs(corner2.DistanceTo(inPt) - corner2.DistanceTo(outPt))
                                ? corner1 : corner2;
                        }

                        if (inPt.DistanceTo(interPt) > 0.01 && interPt.DistanceTo(outPt) > 0.01)
                        {
                            try
                            {
                                XYZ dirIn = (inPt - interPt).Normalize();
                                XYZ dirOut = (outPt - interPt).Normalize();
                                double angle = dirIn.AngleTo(dirOut);
                                if (angle > 0.01 && angle < Math.PI - 0.01)
                                {
                                    XYZ bisector = (dirIn + dirOut).Normalize();
                                    double r = interPt.DistanceTo(inPt);
                                    double radius = r * Math.Tan(angle / 2.0);
                                    double distToCenter = radius / Math.Sin(angle / 2.0);
                                    XYZ arcCenter = interPt + bisector * distToCenter;
                                    XYZ arcMid = arcCenter - bisector * radius;
                                    rawCurves.Add(Arc.Create(inPt, outPt, arcMid));
                                }
                                else
                                    rawCurves.Add(Line.CreateBound(inPt, outPt));
                            }
                            catch
                            {
                                rawCurves.Add(Line.CreateBound(inPt, outPt));
                            }
                        }
                        else
                        {
                            rawCurves.Add(Line.CreateBound(inPt, outPt));
                        }

                        lookAt = outPt;
                    }
                }
                catch { }
            }

            if (rawCurves.Count == 0) return rawCurves;

            // Stitch raw curves
            var stitched = new List<Curve> { rawCurves[0] };
            for (int i = 1; i < rawCurves.Count; i++)
            {
                XYZ pEnd = stitched[stitched.Count - 1].GetEndPoint(1);
                XYZ nStart = rawCurves[i].GetEndPoint(0);
                if (pEnd.DistanceTo(nStart) > 0.001)
                {
                    try { stitched.Add(Line.CreateBound(pEnd, nStart)); } catch { }
                }
                stitched.Add(rawCurves[i]);
            }

            return stitched;
        }

        public static double AdjustZForFloors(Document hostDoc, double x, double y, double zStart, double zEnd)
        {
            double zMin = Math.Min(zStart, zEnd);
            double zMax = Math.Max(zStart, zEnd);
            double adjustedZ = zStart;

            var floorBoxes = new List<BoundingBoxXYZ>();

            try
            {
                // 1. Host floors
                var hostFloors = new FilteredElementCollector(hostDoc)
                    .OfClass(typeof(Floor))
                    .ToList();
                foreach (var f in hostFloors)
                {
                    BoundingBoxXYZ bb = f.get_BoundingBox(null);
                    if (bb != null) floorBoxes.Add(bb);
                }
            }
            catch { }

            try
            {
                // 2. Link floors
                var links = new FilteredElementCollector(hostDoc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>()
                    .ToList();

                foreach (var link in links)
                {
                    try
                    {
                        Document linkDoc = link.GetLinkDocument();
                        if (linkDoc == null) continue;
                        Transform trans = link.GetTotalTransform();

                        var linkFloors = new FilteredElementCollector(linkDoc)
                            .OfClass(typeof(Floor))
                            .ToList();

                        foreach (var lf in linkFloors)
                        {
                            BoundingBoxXYZ bb = lf.get_BoundingBox(null);
                            if (bb == null) continue;

                            XYZ minHost = trans.OfPoint(bb.Min);
                            XYZ maxHost = trans.OfPoint(bb.Max);

                            BoundingBoxXYZ bbHost = new BoundingBoxXYZ
                            {
                                Min = new XYZ(Math.Min(minHost.X, maxHost.X), Math.Min(minHost.Y, maxHost.Y), Math.Min(minHost.Z, maxHost.Z)),
                                Max = new XYZ(Math.Max(minHost.X, maxHost.X), Math.Max(minHost.Y, maxHost.Y), Math.Max(minHost.Z, maxHost.Z))
                            };
                            floorBoxes.Add(bbHost);
                        }
                    }
                    catch { }
                }
            }
            catch { }

            // Process boxes
            foreach (var bb in floorBoxes)
            {
                if (x >= bb.Min.X && x <= bb.Max.X && y >= bb.Min.Y && y <= bb.Max.Y)
                {
                    if (bb.Min.Z < zMax && bb.Max.Z > zMin)
                    {
                        if (zStart > zEnd)
                        {
                            // Cable goes DOWN
                            if (bb.Min.Z > zEnd && zStart >= bb.Min.Z)
                            {
                                double candidate = bb.Min.Z - 0.05;
                                if (candidate < adjustedZ) adjustedZ = candidate;
                            }
                        }
                        else
                        {
                            // Cable goes UP
                            if (zStart >= bb.Min.Z && zStart <= bb.Max.Z)
                            {
                                double candidate = bb.Min.Z - 0.05;
                                if (candidate < adjustedZ) adjustedZ = candidate;
                            }
                        }
                    }
                }
            }

            return adjustedZ;
        }

        // ─────────────────────────────────────────────────────────────
        // TRIM & ADD ORTHOGONAL DROPS
        // ─────────────────────────────────────────────────────────────

        public static List<Curve> TrimAndAddDrops(Document hostDoc, List<Curve> curves, XYZ startPt, XYZ endPt)
        {
            if (curves == null || curves.Count == 0)
            {
                var direct = new List<Curve>();
                if (startPt.DistanceTo(endPt) > 0.001)
                {
                    double adjZStart = hostDoc != null ? AdjustZForFloors(hostDoc, startPt.X, startPt.Y, startPt.Z, endPt.Z) : startPt.Z;
                    XYZ adjStart = new XYZ(startPt.X, startPt.Y, adjZStart);

                    XYZ turn = new XYZ(endPt.X, endPt.Y, adjStart.Z);
                    if (adjStart.DistanceTo(turn) > 0.001) direct.Add(Line.CreateBound(adjStart, turn));
                    if (turn.DistanceTo(endPt) > 0.001) direct.Add(Line.CreateBound(turn, endPt));
                }
                return direct;
            }

            // Find closest segment to start
            int idxStart = 0; double minDS = double.MaxValue; XYZ ptOnStart = curves[0].GetEndPoint(0);
            for (int i = 0; i < curves.Count; i++)
            {
                try { var pr = curves[i].Project(startPt); if (pr != null && pr.Distance < minDS) { minDS = pr.Distance; idxStart = i; ptOnStart = pr.XYZPoint; } } catch { }
            }
            // Find closest segment to end
            int idxEnd = curves.Count - 1; double minDE = double.MaxValue; XYZ ptOnEnd = curves[curves.Count - 1].GetEndPoint(1);
            for (int i = 0; i < curves.Count; i++)
            {
                try { var pr = curves[i].Project(endPt); if (pr != null && pr.Distance < minDE) { minDE = pr.Distance; idxEnd = i; ptOnEnd = pr.XYZPoint; } } catch { }
            }

            // If user selected elements in reverse direction, swap indices AND the original points
            if (idxStart > idxEnd)
            {
                int t = idxStart; idxStart = idxEnd; idxEnd = t;
                XYZ tp = ptOnStart; ptOnStart = ptOnEnd; ptOnEnd = tp;
                XYZ ts = startPt; startPt = endPt; endPt = ts;
            }

            // If start projection is near the beginning of the chain, preserve full first segment
            if (idxStart == 0)
            {
                XYZ chainStart = curves[0].GetEndPoint(0);
                if (startPt.DistanceTo(chainStart) <= startPt.DistanceTo(curves[0].GetEndPoint(1)) ||
                    ptOnStart.DistanceTo(chainStart) < 1.0)
                {
                    ptOnStart = chainStart;
                }
            }

            // If end projection is near the end of the chain, preserve full last segment
            if (idxEnd == curves.Count - 1)
            {
                XYZ chainEnd = curves[idxEnd].GetEndPoint(1);
                if (endPt.DistanceTo(chainEnd) <= endPt.DistanceTo(curves[idxEnd].GetEndPoint(0)) ||
                    ptOnEnd.DistanceTo(chainEnd) < 1.0)
                {
                    ptOnEnd = chainEnd;
                }
            }

            var trimmed = new List<Curve>();
            if (idxStart == idxEnd)
            {
                if (ptOnStart.DistanceTo(ptOnEnd) > 0.001)
                {
                    Curve c = curves[idxStart];
                    if (c is Arc arc)
                    {
                        try
                        {
                            double p1 = arc.Project(ptOnStart).Parameter;
                            double p2 = arc.Project(ptOnEnd).Parameter;
                            trimmed.Add(Arc.Create(ptOnStart, ptOnEnd, arc.Evaluate((p1 + p2) * 0.5, false)));
                        }
                        catch { trimmed.Add(Line.CreateBound(ptOnStart, ptOnEnd)); }
                    }
                    else
                    {
                        try { trimmed.Add(Line.CreateBound(ptOnStart, ptOnEnd)); } catch { }
                    }
                }
            }
            else
            {
                // First segment
                Curve cStart = curves[idxStart];
                XYZ ep1 = cStart.GetEndPoint(1);
                if (ptOnStart.DistanceTo(ep1) > 0.001)
                {
                    if (cStart is Arc arc)
                    {
                        try
                        {
                            double p1 = arc.Project(ptOnStart).Parameter;
                            double p2 = arc.Project(ep1).Parameter;
                            trimmed.Add(Arc.Create(ptOnStart, ep1, arc.Evaluate((p1 + p2) * 0.5, false)));
                        }
                        catch { trimmed.Add(Line.CreateBound(ptOnStart, ep1)); }
                    }
                    else
                    {
                        try { trimmed.Add(Line.CreateBound(ptOnStart, ep1)); } catch { }
                    }
                }

                // Intermediate segments
                for (int i = idxStart + 1; i < idxEnd; i++) trimmed.Add(curves[i]);

                // Last segment
                Curve cEnd = curves[idxEnd];
                XYZ lp0 = cEnd.GetEndPoint(0);
                if (lp0.DistanceTo(ptOnEnd) > 0.001)
                {
                    if (cEnd is Arc arc)
                    {
                        try
                        {
                            double p1 = arc.Project(lp0).Parameter;
                            double p2 = arc.Project(ptOnEnd).Parameter;
                            trimmed.Add(Arc.Create(lp0, ptOnEnd, arc.Evaluate((p1 + p2) * 0.5, false)));
                        }
                        catch { trimmed.Add(Line.CreateBound(lp0, ptOnEnd)); }
                    }
                    else
                    {
                        try { trimmed.Add(Line.CreateBound(lp0, ptOnEnd)); } catch { }
                    }
                }
            }

            var result = new List<Curve>();
            if (trimmed.Count > 0)
            {
                // Orthogonal drop at start
                try
                {
                    XYZ ptTrayStart = trimmed[0].GetEndPoint(0);
                    if (startPt.DistanceTo(ptTrayStart) > 0.001)
                    {
                        double adjZ = hostDoc != null ? AdjustZForFloors(hostDoc, startPt.X, startPt.Y, startPt.Z, ptTrayStart.Z) : startPt.Z;
                        XYZ adjStartPt = new XYZ(startPt.X, startPt.Y, adjZ);

                        XYZ turnStart = new XYZ(adjStartPt.X, adjStartPt.Y, ptTrayStart.Z);
                        if (adjStartPt.DistanceTo(turnStart) > 0.001) result.Add(Line.CreateBound(adjStartPt, turnStart));
                        if (turnStart.DistanceTo(ptTrayStart) > 0.001) result.Add(Line.CreateBound(turnStart, ptTrayStart));
                    }
                }
                catch { }

                result.AddRange(trimmed);

                // Orthogonal drop at end
                try
                {
                    XYZ ptTrayEnd = trimmed[trimmed.Count - 1].GetEndPoint(1);
                    if (ptTrayEnd.DistanceTo(endPt) > 0.001)
                    {
                        double adjZ = hostDoc != null ? AdjustZForFloors(hostDoc, endPt.X, endPt.Y, endPt.Z, ptTrayEnd.Z) : endPt.Z;
                        XYZ adjEndPt = new XYZ(endPt.X, endPt.Y, adjZ);

                        XYZ turnEnd = new XYZ(adjEndPt.X, adjEndPt.Y, ptTrayEnd.Z);
                        if (ptTrayEnd.DistanceTo(turnEnd) > 0.001) result.Add(Line.CreateBound(ptTrayEnd, turnEnd));
                        if (turnEnd.DistanceTo(adjEndPt) > 0.001) result.Add(Line.CreateBound(turnEnd, adjEndPt));
                    }
                }
                catch { }
            }
            else
            {
                try
                {
                    if (startPt.DistanceTo(endPt) > 0.001)
                    {
                        double adjZStart = hostDoc != null ? AdjustZForFloors(hostDoc, startPt.X, startPt.Y, startPt.Z, endPt.Z) : startPt.Z;
                        XYZ adjStart = new XYZ(startPt.X, startPt.Y, adjZStart);

                        XYZ turn = new XYZ(endPt.X, endPt.Y, adjStart.Z);
                        if (adjStart.DistanceTo(turn) > 0.001) result.Add(Line.CreateBound(adjStart, turn));
                        if (turn.DistanceTo(endPt) > 0.001) result.Add(Line.CreateBound(turn, endPt));
                    }
                }
                catch { }
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────
        // OFFSET CURVES (lateral row offset)
        // ─────────────────────────────────────────────────────────────

        public static List<Curve> OffsetCurves(List<Curve> curves, double offsetDistance)
        {
            if (Math.Abs(offsetDistance) <= 0.001) return curves;

            var moved = new List<Curve>();
            foreach (var curve in curves)
            {
                if (curve.Length < 0.001) continue;
                try
                {
                    if (curve is Line line)
                    {
                        XYZ tan = line.ComputeDerivatives(0.0, true).BasisX.Normalize();
                        XYZ offVec = tan.CrossProduct(XYZ.BasisZ).Normalize() * offsetDistance;
                        moved.Add(Line.CreateBound(line.GetEndPoint(0) + offVec, line.GetEndPoint(1) + offVec));
                    }
                    else if (curve is Arc arc)
                    {
                        XYZ center = arc.Center;
                        XYZ tanStart = arc.ComputeDerivatives(0.0, true).BasisX.Normalize();
                        XYZ tanEnd = arc.ComputeDerivatives(1.0, true).BasisX.Normalize();
                        XYZ offStart = tanStart.CrossProduct(XYZ.BasisZ).Normalize() * offsetDistance;
                        XYZ offEnd = tanEnd.CrossProduct(XYZ.BasisZ).Normalize() * offsetDistance;
                        XYZ newStart = arc.GetEndPoint(0) + offStart;
                        XYZ newEnd = arc.GetEndPoint(1) + offEnd;
                        double newRadius = center.DistanceTo(newStart);
                        if (newRadius > 0.01)
                        {
                            XYZ origMid = arc.Evaluate(0.5, true);
                            XYZ dirMid = (origMid - center).Normalize();
                            XYZ newMid = center + dirMid * newRadius;
                            Arc offArc = Arc.Create(newStart, newEnd, newMid);
                            if (offArc != null) moved.Add(offArc);
                        }
                    }
                    else
                    {
                        Curve off = curve.CreateOffset(offsetDistance, XYZ.BasisZ);
                        if (off != null) moved.Add(off);
                    }
                }
                catch { }
            }

            return moved.Count > 0 ? moved : curves;
        }

        // ─────────────────────────────────────────────────────────────
        // STITCH WITH XY INTERSECTION CORRECTION
        // ─────────────────────────────────────────────────────────────

        private static XYZ IntersectionXY(Line l1, Line l2)
        {
            try
            {
                XYZ p1 = l1.GetEndPoint(0), p2 = l1.GetEndPoint(1);
                XYZ p3 = l2.GetEndPoint(0), p4 = l2.GetEndPoint(1);
                double x1 = p1.X, y1 = p1.Y, x2 = p2.X, y2 = p2.Y;
                double x3 = p3.X, y3 = p3.Y, x4 = p4.X, y4 = p4.Y;
                double denom = (x1 - x2) * (y3 - y4) - (y1 - y2) * (x3 - x4);
                if (Math.Abs(denom) < 1e-5) return null;
                double nx = (x1 * y2 - y1 * x2) * (x3 - x4) - (x1 - x2) * (x3 * y4 - y3 * x4);
                double ny = (x1 * y2 - y1 * x2) * (y3 - y4) - (y1 - y2) * (x3 * y4 - y3 * x4);
                return new XYZ(nx / denom, ny / denom, (p2.Z + p3.Z) / 2.0);
            }
            catch { return null; }
        }

        public static List<Curve> StitchCurves(List<Curve> curves)
        {
            if (curves == null || curves.Count == 0) return curves;

            var working = new List<Curve>(curves);
            var corrected = new List<Curve>();
            int i = 0;

            while (i < working.Count)
            {
                if (i == working.Count - 1) { corrected.Add(working[i]); break; }

                Curve curr = working[i];
                Curve next = working[i + 1];

                if (curr is Line lCurr && next is Line lNext)
                {
                    XYZ inter = IntersectionXY(lCurr, lNext);
                    if (inter != null)
                    {
                        try
                        {
                            Line trimmedCurr = Line.CreateBound(lCurr.GetEndPoint(0), inter);
                            Line trimmedNext = Line.CreateBound(inter, lNext.GetEndPoint(1));
                            if (trimmedCurr.Length > 0.001 && trimmedNext.Length > 0.001)
                            {
                                corrected.Add(trimmedCurr);
                                working[i + 1] = trimmedNext;
                                i++;
                                continue;
                            }
                        }
                        catch { }
                    }
                }

                corrected.Add(curr);
                XYZ endCurr = curr.GetEndPoint(1);
                XYZ startNext = next.GetEndPoint(0);
                if (endCurr.DistanceTo(startNext) > 0.001)
                {
                    try { var bridge = Line.CreateBound(endCurr, startNext); if (bridge.Length > 0.001) corrected.Add(bridge); } catch { }
                }
                i++;
            }

            return corrected;
        }

        // ─────────────────────────────────────────────────────────────
        // BUILD SOLIDS (rectangular cross-section extrusion)
        // ─────────────────────────────────────────────────────────────

        /// <param name="halfSize">Half the cable width/height in feet</param>
        public static List<GeometryObject> BuildSolids(List<Curve> finalCurves, double halfSize)
        {
            var solids = new List<GeometryObject>();

            foreach (var curve in finalCurves)
            {
                if (curve.Length < 0.001) continue;
                IList<XYZ> tess = curve.Tessellate();
                for (int i = 0; i < tess.Count - 1; i++)
                {
                    XYZ start = tess[i], end = tess[i + 1];
                    XYZ vec = end - start;
                    double length = vec.GetLength();
                    if (length < 0.001) continue;

                    XYZ direction = vec.Normalize();
                    XYZ up = XYZ.BasisZ;
                    XYZ right = Math.Abs(direction.DotProduct(up)) > 0.999
                        ? XYZ.BasisX
                        : direction.CrossProduct(up).Normalize();
                    up = right.CrossProduct(direction).Normalize();

                    XYZ p1 = start + right * halfSize + up * halfSize;
                    XYZ p2 = start - right * halfSize + up * halfSize;
                    XYZ p3 = start - right * halfSize - up * halfSize;
                    XYZ p4 = start + right * halfSize - up * halfSize;

                    try
                    {
                        var profileList = new List<Curve>
                        {
                            Line.CreateBound(p1, p2),
                            Line.CreateBound(p2, p3),
                            Line.CreateBound(p3, p4),
                            Line.CreateBound(p4, p1)
                        };
                        CurveLoop loop = CurveLoop.Create(profileList);
                        IList<CurveLoop> loops = new List<CurveLoop> { loop };
                        Solid solid = GeometryCreationUtilities.CreateExtrusionGeometry(loops, direction, length);
                        if (solid != null) solids.Add(solid);
                    }
                    catch { }
                }
            }

            return solids;
        }

        public static (List<Curve> Curves, double LengthFt) BuildRoute(
            Document hostDoc,
            IList<Element> routeElements,
            XYZ startPt,
            XYZ endPt,
            double offsetFt,
            double minSegmentFt = 0.082)
        {
            var sorted = SortByGeometry(routeElements, startPt);
            var raw = GetContinuousCenterline(sorted, startPt, endPt);

            // Filter very short segments
            var filtered = raw.Where(c => !(c is Line) || c.Length >= minSegmentFt).ToList();
            if (filtered.Count == 0 && raw.Count > 0) filtered = raw;

            var moved = OffsetCurves(filtered, offsetFt);
            var stitched = StitchCurves(moved);
            var final = TrimAndAddDrops(hostDoc, stitched, startPt, endPt);

            double totalFt = final.Sum(c => c.Length);
            return (final, totalFt);
        }
    }
}
