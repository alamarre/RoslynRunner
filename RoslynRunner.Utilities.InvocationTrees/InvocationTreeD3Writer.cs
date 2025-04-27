using System;

namespace RoslynRunner.Utilities.InvocationTrees;

internal class InvocationTreeD3Writer
{
    public static string GetD3GraphForCallers(IEnumerable<InvocationMethod> methods)
    {
        string jsonData = InvocationTreeJsonWriter.WriteInvocationTreeToJson(methods);
        string htmlContent = $$"""
<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <title>Hierarchical Call Graph (Top-Down)</title>
  <style>
    body { margin: 0; overflow: hidden; font-family: sans-serif; }
    svg { width: 100vw; height: 100vh; background: #fafafa; cursor: move; }
    .link { fill: none; stroke: #555; stroke-width: 1.5px; }
    .node circle { fill: #999; stroke: #555; stroke-width: 1.5px; }
    .node text { font: 12px sans-serif; pointer-events: none; }
    .tooltip {
      position: absolute; padding: 4px 8px;
      background: rgba(0,0,0,0.7); color: #fff;
      font-size: 12px; pointer-events: none;
      border-radius: 4px; opacity: 0;
      transition: opacity 0.1s;
    }
  </style>
</head>
<body>
  <div class="tooltip" id="tooltip"></div>
  <svg></svg>
  <script src="https://d3js.org/d3.v7.min.js"></script>
  <script>
    const svg = d3.select("svg");
    const width = window.innerWidth;
    const height = window.innerHeight;
    const tooltip = d3.select('#tooltip');

    const jsonData = {{jsonData}}  // Embedded JSON data

    const map = new Map(jsonData.map(d => [d.identifier, d]));

    // recursive build with global dedupe
    const visitedGlobal = new Set();
    function buildNode(id) {
      if (visitedGlobal.has(id) || !map.has(id)) return null;
      visitedGlobal.add(id);
      const d = map.get(id);
      const childrenIds = [].concat(d.invokedMethodIdentifiers || [], d.implementationIdentifiers || []);
      const children = childrenIds.map(buildNode).filter(c => c !== null);
      return { id, data: d, children };
    }

    const rootId = jsonData[0]?.identifier;
    const rootObj = buildNode(rootId);
    const root = d3.hierarchy(rootObj, d => d.children);

    // margins & layout
    const margin = {top: 20, right: 20, bottom: 20, left: 20 };
    // vertical tree: siblings horizontal spacing = 100, depth vertical spacing = 100
    const tree = d3.tree().nodeSize([100, 100]);
    tree(root);

    // zoomable group
    const zoomGroup = svg.append('g');
    const g = zoomGroup.append('g')
      .attr('transform', `translate(${margin.left},${margin.top})`);

    svg.call(
      d3.zoom()
        .scaleExtent([0.5, 3])
        .on('zoom', event => zoomGroup.attr('transform', event.transform))
    );

    // draw links with vertical orientation
    g.selectAll('.link')
      .data(root.links())
      .join('path')
      .attr('class', 'link')
      .attr('d', d3.linkVertical()
        .x(d => d.x)
        .y(d => d.y)
      );

    // draw nodes top-down
    const node = g.selectAll('.node')
      .data(root.descendants())
      .join('g')
      .attr('class', 'node')
      .attr('transform', d => `translate(${d.x},${d.y})`)
      .on('mouseover', (event, d) => {tooltip.style('opacity', 1)
          .html(`<strong>${d.data.data.containingTypeFqdn}</strong><br>${d.data.data.methodSignature}`)
          .style('left', (event.pageX + 10) + 'px')
          .style('top', (event.pageY + 10) + 'px');
      })
      .on('mouseout', () => tooltip.style('opacity', 0));

    node.append('circle').attr('r', 6);
    node.append('text')
      .attr('dy', '0.31em')
      .attr('y', d => d.children ? -10 : 10)
      .attr('text-anchor', 'middle')
      .text(d => d.data.data.methodName)
      .clone(true).lower().attr('stroke', 'white');
  </script>
</body>
</html>
""";
        return htmlContent;

    }
}
