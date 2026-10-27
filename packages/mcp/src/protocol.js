/**
 * Minimal, spec-compliant MCP server over stdio.
 *
 * Zero dependencies: the Model Context Protocol is JSON-RPC 2.0 over
 * newline-delimited JSON on stdin/stdout, which is small enough to
 * implement directly and keeps startup latency at zero.
 */
import { createInterface } from 'node:readline';

export const PROTOCOL_VERSION = '2025-06-18';

export class McpServer {
  /**
   * @param {object} opts
   * @param {string} [opts.name]
   * @param {string} [opts.version]
   * @param {string} [opts.instructions]
   */
  constructor({ name = 'agbrowser', version = '0.1.0', instructions = '' } = {}) {
    this.name = name;
    this.version = version;
    this.instructions = instructions;
    /** @type {Map<string, {name:string,description:string,inputSchema:object,handler:Function}>} */
    this.tools = new Map();
    this.initialized = false;
    this._rl = null;
    this._write = null;
  }

  /**
   * Register a tool.
   * @param {object} spec
   * @param {string} spec.name
   * @param {string} spec.description
   * @param {object} spec.inputSchema JSON Schema
   * @param {(args:object)=>Promise<any>} spec.handler
   */
  tool({ name, description, inputSchema, handler }) {
    this.tools.set(name, { name, description, inputSchema, handler });
    return this;
  }

  /** Start serving on stdin/stdout. */
  listen() {
    this._rl = createInterface({ input: process.stdin, crlfDelay: Infinity });

    this._rl.on('line', async (line) => {
      const trimmed = line.trim();
      if (!trimmed) return;
      let msg;
      try {
        msg = JSON.parse(trimmed);
      } catch {
        this._send({ jsonrpc: '2.0', id: null, error: { code: -32700, message: 'Parse error' } });
        return;
      }
      await this._handle(msg);
    });

    process.stdin.on('end', () => process.exit(0));
    return this;
  }

  async _handle(msg) {
    const { id, method, params } = msg;

    // Notifications carry no id and must not be answered.
    const isNotification = id === undefined || id === null;

    try {
      switch (method) {
        case 'initialize': {
          this.initialized = true;
          this._send({
            jsonrpc: '2.0',
            id,
            result: {
              protocolVersion: negotiateVersion(params?.protocolVersion),
              capabilities: { tools: { listChanged: false } },
              serverInfo: { name: this.name, version: this.version },
              ...(this.instructions ? { instructions: this.instructions } : {}),
            },
          });
          return;
        }

        case 'notifications/initialized':
        case 'initialized':
          return;

        case 'ping':
          if (!isNotification) this._send({ jsonrpc: '2.0', id, result: {} });
          return;

        case 'tools/list':
          this._send({
            jsonrpc: '2.0',
            id,
            result: {
              tools: [...this.tools.values()].map((t) => ({
                name: t.name,
                description: t.description,
                inputSchema: t.inputSchema,
              })),
            },
          });
          return;

        case 'tools/call': {
          const tool = this.tools.get(params?.name);
          if (!tool) {
            this._send({
              jsonrpc: '2.0',
              id,
              result: {
                content: [{ type: 'text', text: `Unknown tool: ${params?.name}` }],
                isError: true,
              },
            });
            return;
          }
          try {
            const out = await tool.handler(params?.arguments ?? {});
            this._send({ jsonrpc: '2.0', id, result: normalizeToolResult(out) });
          } catch (err) {
            this._send({
              jsonrpc: '2.0',
              id,
              result: {
                content: [{ type: 'text', text: `Error: ${err?.message ?? String(err)}` }],
                isError: true,
              },
            });
          }
          return;
        }

        case 'resources/list':
          this._send({ jsonrpc: '2.0', id, result: { resources: [] } });
          return;

        case 'prompts/list':
          this._send({ jsonrpc: '2.0', id, result: { prompts: [] } });
          return;

        default:
          if (!isNotification) {
            this._send({
              jsonrpc: '2.0',
              id,
              error: { code: -32601, message: `Method not found: ${method}` },
            });
          }
      }
    } catch (err) {
      if (!isNotification) {
        this._send({
          jsonrpc: '2.0',
          id,
          error: { code: -32603, message: err?.message ?? 'Internal error' },
        });
      }
    }
  }

  _send(payload) {
    const data = JSON.stringify(payload);
    process.stdout.write(`${data}\n`);
  }
}

const KNOWN_VERSIONS = ['2025-06-18', '2025-03-26', '2024-11-05'];

/** Echo back the client's version when we understand it, else offer ours. */
function negotiateVersion(clientVersion) {
  if (clientVersion && KNOWN_VERSIONS.includes(clientVersion)) return clientVersion;
  return PROTOCOL_VERSION;
}

/**
 * Normalize a tool handler's return value into MCP content blocks.
 * Objects become JSON text; images become image content; strings pass through.
 */
function normalizeToolResult(out) {
  if (out === undefined || out === null) {
    return { content: [{ type: 'text', text: 'OK' }] };
  }
  if (typeof out === 'string') {
    return { content: [{ type: 'text', text: out }] };
  }
  if (out && typeof out === 'object' && Array.isArray(out.content)) {
    return out;
  }
  if (out && typeof out === 'object' && out.type === 'image') {
    return { content: [out] };
  }
  return {
    content: [{ type: 'text', text: JSON.stringify(out, null, 2) }],
  };
}
