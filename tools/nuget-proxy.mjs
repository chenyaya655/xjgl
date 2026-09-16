// 本地 NuGet 镜像代理
// ---------------------------------------------------------------------------
// 为什么需要它：
//   在本机受限沙箱（workspace-write）下，Windows Schannel 无法获取加密凭据
//   （SEC_E_NO_CREDENTIALS），导致 dotnet restore / curl / git(schannel) 的
//   HTTPS 全部失败。Node.js 自带 OpenSSL，不受该限制。
//
//   本脚本把 https://api.nuget.org 反向代理到 http://127.0.0.1:5555，
//   并把响应 JSON 里的上游地址改写成回环地址，于是 dotnet 只需明文 HTTP
//   即可完成还原 —— 不再触碰 Schannel。
//
// 用法：
//   node tools/nuget-proxy.mjs
//   dotnet restore --configfile .tools/nuget.local.config
//
// 注意：这是本地开发辅助工具，与业务代码无关，不参与生产部署。
//       你自己的 VS / 终端有正常网络时，直接走 nuget.org 即可，无需本代理。

import http from 'node:http';

const UPSTREAM = process.env.NUGET_UPSTREAM || 'https://api.nuget.org';
const HOST = process.env.PROXY_HOST || '127.0.0.1';
const PORT = Number(process.env.PROXY_PORT || 5555);
const PUBLIC_BASE = `http://${HOST}:${PORT}`;

const silent = process.argv.includes('--quiet');

const server = http.createServer(async (req, res) => {
  const target = UPSTREAM + req.url;

  try {
    const upstream = await fetch(target, {
      method: req.method,
      headers: {
        'user-agent': 'xjgl-nuget-proxy/1.0',
        // 要求上游不要压缩，方便对 JSON 做地址改写
        'accept-encoding': 'identity',
      },
      redirect: 'follow',
    });

    const contentType = upstream.headers.get('content-type') || 'application/octet-stream';
    const raw = Buffer.from(await upstream.arrayBuffer());

    let body = raw;
    if (contentType.includes('json') || contentType.includes('text')) {
      let text = raw.toString('utf8');

      // NuGet 强制要求“存储库签名”资源的地址是 HTTPS。
      // 我们走的是明文回环地址，因此把该资源从服务索引里摘掉，
      // 让 NuGet 跳过仓库签名校验（仅影响本地开发环境）。
      if (req.url.startsWith('/v3/index.json')) {
        try {
          const doc = JSON.parse(text);
          if (Array.isArray(doc.resources)) {
            const before = doc.resources.length;
            doc.resources = doc.resources.filter(
              (r) => !String(r['@type'] || '').startsWith('RepositorySignatures'),
            );
            if (!silent && before !== doc.resources.length) {
              process.stdout.write(`  (stripped ${before - doc.resources.length} RepositorySignatures resource)\n`);
            }
          }
          text = JSON.stringify(doc);
        } catch {
          // 结构非预期时保持原文
        }
      }

      text = text.split(UPSTREAM).join(PUBLIC_BASE);
      body = Buffer.from(text, 'utf8');
    }

    res.writeHead(upstream.status, {
      'content-type': contentType,
      'content-length': body.length,
      'cache-control': 'no-store',
    });
    res.end(body);

    if (!silent) process.stdout.write(`${upstream.status} ${req.method} ${req.url}\n`);
  } catch (err) {
    res.writeHead(502, { 'content-type': 'text/plain; charset=utf-8' });
    res.end(`nuget proxy error: ${err.message}`);
    process.stderr.write(`502 ${req.method} ${req.url} :: ${err.message}\n`);
  }
});

server.listen(PORT, HOST, () => {
  process.stdout.write(`nuget proxy listening on ${PUBLIC_BASE}  ->  ${UPSTREAM}\n`);
});

for (const sig of ['SIGINT', 'SIGTERM']) {
  process.on(sig, () => {
    server.close(() => process.exit(0));
  });
}
