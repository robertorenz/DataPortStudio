using System.Data.Common;
using System.Text.Json;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using DataPortStudio.Models;

namespace DataPortStudio.Services;

public record ErColumn(string Name, string Type, bool IsPk);
public record ErTable(string Name, IReadOnlyList<ErColumn> Columns);
public record ErForeignKey(string From, string FromCol, string To, string ToCol);

public static class ErDiagramService
{
    public static async Task<(List<ErTable> Tables, List<ErForeignKey> Fks)> LoadAsync(
        ConnectionProfile connection, string? database, string schema = "dbo")
    {
        var cs = connection.BuildConnectionString();
        await using var conn = OpenConnection(connection, cs, database);
        await conn.OpenAsync();

        var tables = await LoadTablesAsync(conn, connection.Engine, database, schema);
        var fks    = await LoadFksAsync(conn, connection.Engine, database, schema);
        return (tables, fks);
    }

    private static async Task<List<ErTable>> LoadTablesAsync(
        DbConnection conn, DatabaseEngine engine, string? database, string schema)
    {
        var sql = engine switch
        {
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                $"SELECT TABLE_NAME, COLUMN_NAME, DATA_TYPE, COLUMN_KEY " +
                $"FROM INFORMATION_SCHEMA.COLUMNS " +
                $"WHERE TABLE_SCHEMA = '{database}' ORDER BY TABLE_NAME, ORDINAL_POSITION",
            DatabaseEngine.Sqlite =>
                "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name",
            _ =>
                $"SELECT c.TABLE_NAME, c.COLUMN_NAME, c.DATA_TYPE, " +
                $"  CASE WHEN pk.COLUMN_NAME IS NOT NULL THEN 'PRI' ELSE '' END AS COL_KEY " +
                $"FROM INFORMATION_SCHEMA.COLUMNS c " +
                $"LEFT JOIN ( " +
                $"  SELECT ku.TABLE_NAME, ku.COLUMN_NAME FROM INFORMATION_SCHEMA.TABLE_CONSTRAINTS tc " +
                $"  JOIN INFORMATION_SCHEMA.KEY_COLUMN_USAGE ku " +
                $"    ON tc.CONSTRAINT_NAME = ku.CONSTRAINT_NAME AND tc.TABLE_SCHEMA = ku.TABLE_SCHEMA " +
                $"  WHERE tc.CONSTRAINT_TYPE = 'PRIMARY KEY' AND tc.TABLE_SCHEMA = '{schema}' " +
                $") pk ON pk.TABLE_NAME = c.TABLE_NAME AND pk.COLUMN_NAME = c.COLUMN_NAME " +
                $"JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_NAME = c.TABLE_NAME AND t.TABLE_SCHEMA = c.TABLE_SCHEMA " +
                $"WHERE c.TABLE_SCHEMA = '{schema}' AND t.TABLE_TYPE = 'BASE TABLE' " +
                $"ORDER BY c.TABLE_NAME, c.ORDINAL_POSITION"
        };

        // SQLite: special handling
        if (engine == DatabaseEngine.Sqlite)
            return await LoadSqliteTablesAsync(conn);

        var dict = new Dictionary<string, List<ErColumn>>(StringComparer.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            var tbl  = r.GetString(0);
            var col  = r.GetString(1);
            var type = r.GetString(2);
            var isPk = r.GetString(3) == "PRI";
            if (!dict.ContainsKey(tbl)) dict[tbl] = [];
            dict[tbl].Add(new ErColumn(col, type, isPk));
        }
        return dict.Select(kv => new ErTable(kv.Key, kv.Value)).ToList();
    }

    private static async Task<List<ErTable>> LoadSqliteTablesAsync(DbConnection conn)
    {
        var tables = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) tables.Add(r.GetString(0));
        }
        var result = new List<ErTable>();
        foreach (var tbl in tables)
        {
            var cols = new List<ErColumn>();
            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = $"PRAGMA table_info([{tbl}])";
            await using var r2 = await cmd2.ExecuteReaderAsync();
            while (await r2.ReadAsync())
                cols.Add(new ErColumn(r2.GetString(1), r2.GetString(2), r2.GetInt32(5) > 0));
            result.Add(new ErTable(tbl, cols));
        }
        return result;
    }

    private static async Task<List<ErForeignKey>> LoadFksAsync(
        DbConnection conn, DatabaseEngine engine, string? database, string schema)
    {
        if (engine == DatabaseEngine.Sqlite)
            return await LoadSqliteFksAsync(conn);

        var sql = engine switch
        {
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                $"SELECT TABLE_NAME, COLUMN_NAME, REFERENCED_TABLE_NAME, REFERENCED_COLUMN_NAME " +
                $"FROM INFORMATION_SCHEMA.KEY_COLUMN_USAGE " +
                $"WHERE TABLE_SCHEMA = '{database}' AND REFERENCED_TABLE_NAME IS NOT NULL",
            _ =>
                $"SELECT tp.name, cp.name, tr.name, cr.name " +
                $"FROM sys.foreign_keys fk " +
                $"JOIN sys.foreign_key_columns fkc ON fk.object_id = fkc.constraint_object_id " +
                $"JOIN sys.tables tp  ON fkc.parent_object_id     = tp.object_id " +
                $"JOIN sys.columns cp ON fkc.parent_object_id     = cp.object_id AND fkc.parent_column_id     = cp.column_id " +
                $"JOIN sys.tables tr  ON fkc.referenced_object_id = tr.object_id " +
                $"JOIN sys.columns cr ON fkc.referenced_object_id = cr.object_id AND fkc.referenced_column_id = cr.column_id " +
                $"JOIN sys.schemas s  ON tp.schema_id = s.schema_id WHERE s.name = '{schema}'"
        };

        var list = new List<ErForeignKey>();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync())
                list.Add(new ErForeignKey(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        }
        catch { /* FK query not supported — return empty */ }
        return list;
    }

    private static async Task<List<ErForeignKey>> LoadSqliteFksAsync(DbConnection conn)
    {
        var tables = new List<string>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
            await using var r = await cmd.ExecuteReaderAsync();
            while (await r.ReadAsync()) tables.Add(r.GetString(0));
        }
        var fks = new List<ErForeignKey>();
        foreach (var tbl in tables)
        {
            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = $"PRAGMA foreign_key_list([{tbl}])";
            await using var r2 = await cmd2.ExecuteReaderAsync();
            while (await r2.ReadAsync())
                fks.Add(new ErForeignKey(tbl, r2.GetString(3), r2.GetString(2), r2.GetString(4)));
        }
        return fks;
    }

    private static DbConnection OpenConnection(ConnectionProfile c, string cs, string? db) =>
        c.Engine switch
        {
            DatabaseEngine.MySql or DatabaseEngine.MariaDb =>
                new MySqlConnection(string.IsNullOrEmpty(db) ? cs : MySqlService.WithDatabase(cs, db)),
            DatabaseEngine.Sqlite   => new SqliteConnection(cs),
            DatabaseEngine.Firebird => new FbConnection(cs),
            _                       => new SqlConnection(string.IsNullOrEmpty(db) ? cs : SqlServerService.WithDatabase(cs, db))
        };

    // ── HTML generation ──────────────────────────────────────────────────────

    public static string BuildHtml(List<ErTable> tables, List<ErForeignKey> fks)
    {
        var data = new
        {
            tables = tables.Select(t => new
            {
                name    = t.Name,
                columns = t.Columns.Select(c => new { name = c.Name, type = c.Type, isPk = c.IsPk })
            }),
            fks = fks.Select(f => new { from = f.From, fromCol = f.FromCol, to = f.To, toCol = f.ToCol })
        };
        var json = JsonSerializer.Serialize(data);
        return HtmlTemplate.Replace("__DATA__", json);
    }

    private const string HtmlTemplate = """
<!DOCTYPE html><html><head><meta charset="utf-8"/>
<style>
*{margin:0;padding:0;box-sizing:border-box}
body{background:#0d1117;overflow:hidden;font-family:Consolas,monospace}
canvas{display:block;cursor:grab}
canvas.grabbing{cursor:grabbing}
#empty{position:absolute;top:50%;left:50%;transform:translate(-50%,-50%);color:#484f58;font-size:14px;text-align:center;line-height:2}
#hint{position:absolute;bottom:12px;right:16px;color:#484f58;font-size:11px}
</style></head><body>
<canvas id="c"></canvas>
<div id="empty" style="display:none">No tables found in this schema.<br>Try selecting a different database.</div>
<div id="hint">Scroll to zoom · Drag table to move · Drag background to pan</div>
<script>
const DATA=__DATA__;
const cv=document.getElementById('c'),ctx=cv.getContext('2d');
if(!DATA.tables.length)document.getElementById('empty').style.display='block';

const ROW=22,HDR=30,PAD=12,MINW=180;
const C={bg:'#0d1117',nodeBg:'#161b22',hdr:'#21262d',hdrBorder:'#30363d',
         border:'#30363d',tblName:'#58a6ff',colName:'#c9d1d9',colType:'#8b949e',
         pk:'#e3b341',fkLine:'#388bfd',arrow:'#388bfd',rowAlt:'rgba(255,255,255,0.03)'};

const nodes=DATA.tables.map(t=>{
  const allText=t.columns.map(c=>c.name+' '+c.type);
  const maxLen=Math.max(t.name.length*9,allText.reduce((m,s)=>Math.max(m,s.length*7),0))+PAD*2+24;
  const w=Math.max(MINW,maxLen);
  const h=HDR+t.columns.length*ROW+6;
  return{...t,x:0,y:0,w,h,vx:0,vy:0};
});

// grid init
const gridCols=Math.max(1,Math.ceil(Math.sqrt(nodes.length*1.4)));
nodes.forEach((n,i)=>{n.x=60+(i%gridCols)*280;n.y=60+Math.floor(i/gridCols)*240});

// force layout
function layout(iters){
  for(let it=0;it<iters;it++){
    for(let i=0;i<nodes.length;i++)for(let j=i+1;j<nodes.length;j++){
      let dx=nodes[j].x-nodes[i].x,dy=nodes[j].y-nodes[i].y,d2=dx*dx+dy*dy+1,f=90000/d2;
      nodes[i].vx-=dx*f;nodes[i].vy-=dy*f;nodes[j].vx+=dx*f;nodes[j].vy+=dy*f;
    }
    for(const fk of DATA.fks){
      const a=nodes.find(n=>n.name===fk.from),b=nodes.find(n=>n.name===fk.to);
      if(!a||!b||a===b)continue;
      let dx=b.x-a.x,dy=b.y-a.y,d=Math.sqrt(dx*dx+dy*dy)||1,f=(d-300)*0.04;
      a.vx+=dx/d*f;a.vy+=dy/d*f;b.vx-=dx/d*f;b.vy-=dy/d*f;
    }
    for(const n of nodes){n.x+=n.vx*.3;n.y+=n.vy*.3;n.vx*=.7;n.vy*=.7;n.x=Math.max(0,n.x);n.y=Math.max(0,n.y)}
  }
}

let vx=0,vy=0,vs=1;
function toW(sx,sy){return[sx/vs-vx,sy/vs-vy]}

function rrect(x,y,w,h,r){
  ctx.beginPath();ctx.moveTo(x+r,y);ctx.lineTo(x+w-r,y);ctx.quadraticCurveTo(x+w,y,x+w,y+r);
  ctx.lineTo(x+w,y+h-r);ctx.quadraticCurveTo(x+w,y+h,x+w-r,y+h);
  ctx.lineTo(x+r,y+h);ctx.quadraticCurveTo(x,y+h,x,y+h-r);
  ctx.lineTo(x,y+r);ctx.quadraticCurveTo(x,y,x+r,y);ctx.closePath();
}

function draw(){
  cv.width=window.innerWidth;cv.height=window.innerHeight;
  ctx.fillStyle=C.bg;ctx.fillRect(0,0,cv.width,cv.height);
  if(!nodes.length)return;
  ctx.save();ctx.scale(vs,vs);ctx.translate(vx,vy);

  // FK edges
  for(const fk of DATA.fks){
    const a=nodes.find(n=>n.name===fk.from),b=nodes.find(n=>n.name===fk.to);
    if(!a||!b)continue;
    const ai=a.columns.findIndex(c=>c.name===fk.fromCol);
    const bi=b.columns.findIndex(c=>c.name===fk.toCol);
    const ay=a.y+HDR+(ai<0?HDR/2:ai*ROW+ROW/2);
    const by=b.y+HDR+(bi<0?HDR/2:bi*ROW+ROW/2);
    const goRight=a.x+a.w/2<b.x+b.w/2;
    const ax=goRight?a.x+a.w:a.x,bx=goRight?b.x:b.x+b.w;
    const cp=Math.abs(ax-bx)*.45;
    ctx.beginPath();ctx.moveTo(ax,ay);
    ctx.bezierCurveTo(ax+(goRight?cp:-cp),ay,bx+(goRight?-cp:cp),by,bx,by);
    ctx.strokeStyle=C.fkLine;ctx.lineWidth=1.5;ctx.globalAlpha=.55;ctx.stroke();ctx.globalAlpha=1;
    const dir=goRight?1:-1;
    ctx.beginPath();ctx.moveTo(bx,by);ctx.lineTo(bx-dir*9,by-4);ctx.lineTo(bx-dir*9,by+4);
    ctx.closePath();ctx.fillStyle=C.arrow;ctx.fill();
  }

  // Nodes
  for(const n of nodes){
    ctx.shadowColor='rgba(0,0,0,.5)';ctx.shadowBlur=10;ctx.shadowOffsetY=4;
    rrect(n.x,n.y,n.w,n.h,6);ctx.fillStyle=C.nodeBg;ctx.fill();
    ctx.strokeStyle=C.border;ctx.lineWidth=1;ctx.stroke();
    ctx.shadowColor='transparent';ctx.shadowBlur=0;ctx.shadowOffsetY=0;

    // header
    rrect(n.x,n.y,n.w,HDR,6);ctx.fillStyle=C.hdr;ctx.fill();
    ctx.beginPath();ctx.moveTo(n.x,n.y+HDR);ctx.lineTo(n.x+n.w,n.y+HDR);
    ctx.strokeStyle=C.hdrBorder;ctx.lineWidth=1;ctx.stroke();
    ctx.fillStyle=C.tblName;ctx.font='bold 13px Consolas';
    ctx.fillText(n.name,n.x+PAD,n.y+20);

    // columns
    ctx.font='12px Consolas';
    n.columns.forEach((c,i)=>{
      const cy=n.y+HDR+i*ROW;
      if(i%2===0){ctx.fillStyle=C.rowAlt;ctx.fillRect(n.x+1,cy,n.w-2,ROW)}
      const pkMark=c.isPk?'🔑':'  ';
      ctx.fillStyle=c.isPk?C.pk:C.colName;
      ctx.fillText((c.isPk?'⬡ ':'  ')+c.name,n.x+PAD,cy+15);
      const tw=ctx.measureText(c.type).width;
      ctx.fillStyle=C.colType;ctx.fillText(c.type,n.x+n.w-PAD-tw,cy+15);
    });
  }
  ctx.restore();
}

// interaction
let panning=false,panStart=null,dragging=null,dragOff=null;
cv.addEventListener('mousedown',e=>{
  const[wx,wy]=toW(e.offsetX,e.offsetY);
  const hit=[...nodes].reverse().find(n=>wx>=n.x&&wx<=n.x+n.w&&wy>=n.y&&wy<=n.y+n.h);
  if(hit){dragging=hit;dragOff=[wx-hit.x,wy-hit.y];cv.classList.add('grabbing')}
  else{panning=true;panStart=[e.offsetX-vx*vs,e.offsetY-vy*vs];cv.classList.add('grabbing')}
});
cv.addEventListener('mousemove',e=>{
  if(dragging){const[wx,wy]=toW(e.offsetX,e.offsetY);dragging.x=wx-dragOff[0];dragging.y=wy-dragOff[1];draw()}
  else if(panning){vx=(e.offsetX-panStart[0])/vs;vy=(e.offsetY-panStart[1])/vs;draw()}
});
cv.addEventListener('mouseup',()=>{dragging=null;panning=false;cv.classList.remove('grabbing')});
cv.addEventListener('wheel',e=>{
  e.preventDefault();
  const f=e.deltaY<0?1.1:.9;
  const[wx,wy]=toW(e.offsetX,e.offsetY);
  vs=Math.max(.15,Math.min(3,vs*f));vx=e.offsetX/vs-wx;vy=e.offsetY/vs-wy;draw();
},{passive:false});
window.addEventListener('resize',draw);

layout(300);draw();
</script></body></html>
""";
}
