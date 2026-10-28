/**
 * MCP server implementing the 2026-07-28 specification.
 *
 * The 2026-07-28 revision replaced the handshake with a stateless core: every
 * request carries its protocol version and the client's identity in
 * `params._meta`, and any request may be served by any instance. The
 * `initialize` / `initialized` exchange is gone.
 *
 * Zero dependencies: the Model Context Protocol is JSON-RPC 2.0 over
 * newline-delimited JSON on stdin/stdout, which is small enough to
 * implement directly and keeps startup latency at zero.
 */
import { createInterface } from 'node:readline';

/** The protocol revision this server implements. */
export const PROTOCOL_VERSION = '2026-07-28';

/** Every revision this server can serve. */
export const SUPPORTED_VERSIONS = [PROTOCOL_VERSION];

/** Reserved `_meta` keys defined by the specification. */
export const META_KEYS = {
  PROTOCOL_VERSION: 'io.modelcontextprotocol/protocolVersion',
  CLIENT_INFO: 'io.modelcontextprotocol/clientInfo',
  CLIENT_CAPABILITIES: 'io.modelcontextprotocol/clientCapabilities',
  SERVER_INFO: 'io.modelcontextprotocol/serverInfo',
  LOG_LEVEL: 'io.modelcontextprotocol/logLevel',
  PROGRESS_TOKEN: 'io.modelcontextprotocol/progressToken',
};

/** JSON-RPC error code for an unsupported protocol version. */
export const UNSUPPORTED_VERSION_ERROR = -32022;

export class McpServer {
  /**
   * @param {object} opts
   * @param {string} [opts.name]
   * @param {string} [opts.version]
   * @param {string} [opts.instructions]
   * @param {number} [opts.listTtlMs] cache lifetime advertised on list results
   * @param {string} [opts.cacheScope] cache scope advertised on list results
   */
  constructor({
    name = 'agbrowser',
    version = '0.1.0',
    instructions = '',
    listTtlMs = 3_600_000,
    cacheScope = 'public',
  } = {}) {
    this.name = name;
    this.version = version;
    this.instructions = instructions;
    this.listTtlMs = listTtlMs;
    this.cacheScope = cacheScope;
    /** @type {Map<string, {name:string,title?:string,description:string,inputSchema:object,outputSchema?:object,annotations?:object,handler:Function}>} */
    this.tools = new Map();
    this._rl = null;
  }

  /**
   * Register a tool.
   * @param {object} spec
   * @param {string} spec.name
   * @param {string} [spec.title] display name for user interfaces
   * @param {string} spec.description
   * @param {object} spec.inputSchema JSON Schema (2020-12 by default)
   * @param {object} [spec.outputSchema] JSON Schema for the structured result
   * @param {object} [spec.annotations] behavioural hints for the client
   * @param {(args:object)=>Promise<any>} spec.handler
   */
  tool({ name, title, description, inputSchema, outputSchema, annotations, handler }) {
    this.tools.set(name, {
      name,
      title,
      description,
      inputSchema,
      outputSchema,
      annotations,
      handler,
    });
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

  /**
   * Handle one JSON-RPC message.
   *
   * Notifications carry no id and are never answered.
   */
  async _handle(msg) {
    const { id, method, params } = msg;
    const isNotification = id === undefined || id === null;

    try {
      // Discovery is answered before the version check: it is how a client
      // learns which versions exist in the first place.
      if (method === 'server/discover') {
        if (!isNotification) this._send({ jsonrpc: '2.0', id, result: this._discoverResult() });
        return;
      }

      // The legacy handshake is answered before the version check too. A
      // client that only speaks the old revision has no fall-forward
      // mechanism, so the error is its only diagnostic and must name the
      // versions this server does speak.
      if (method === 'initialize' || method === 'initialized') {
        if (!isNotification) {
          this._send({
            jsonrpc: '2.0',
            id,
            error: {
              code: -32601,
              message: `Method not found: ${method}. This server implements MCP ${SUPPORTED_VERSIONS.join(', ')}; the initialize handshake was removed in that revision.`,
              data: { supported: SUPPORTED_VERSIONS },
            },
          });
        }
        return;
      }

      const versionError = this._checkVersion(params);
      if (versionError) {
        if (!isNotification) {
          this._send({ jsonrpc: '2.0', id, error: versionError });
        }
        return;
      }

      switch (method) {
        case 'ping':
          if (!isNotification) {
            this._send({ jsonrpc: '2.0', id, result: this._result({}) });
          }
          return;

        case 'tools/list':
          this._send({
            jsonrpc: '2.0',
            id,
            result: this._result({
              tools: [...this.tools.values()].map((t) => this._toolDefinition(t)),
            }, { cacheable: true }),
          });
          return;

        case 'tools/call': {
          const tool = this.tools.get(params?.name);
          if (!tool) {
            this._send({
              jsonrpc: '2.0',
              id,
              result: this._result({
                content: [{ type: 'text', text: `Unknown tool: ${params?.name}` }],
              }, { isError: true }),
            });
            return;
          }
          try {
            const out = await tool.handler(params?.arguments ?? {});
            this._send({
              jsonrpc: '2.0',
              id,
              result: this._result(normalizeToolResult(out)),
            });
          } catch (err) {
            this._send({
              jsonrpc: '2.0',
              id,
              result: this._result({
                content: [{ type: 'text', text: `Error: ${err?.message ?? String(err)}` }],
              }, { isError: true }),
            });
          }
          return;
        }

        case 'resources/list':
          this._send({
            jsonrpc: '2.0',
            id,
            result: this._result({ resources: [] }, { cacheable: true }),
          });
          return;

        case 'prompts/list':
          this._send({
            jsonrpc: '2.0',
            id,
            result: this._result({ prompts: [] }, { cacheable: true }),
          });
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

  /**
   * Validate the protocol version carried in `params._meta`.
   *
   * @returns {object|null} a JSON-RPC error object, or null when acceptable
   */
  _checkVersion(params) {
    const meta = params?._meta;
    const requested = meta?.[META_KEYS.PROTOCOL_VERSION];

    if (requested === undefined || requested === null) {
      // A request without a declared version cannot be served: the revision
      // defines the semantics the client expects.
      return {
        code: UNSUPPORTED_VERSION_ERROR,
        message: 'Unsupported protocol version',
        data: { supported: SUPPORTED_VERSIONS, requested: null },
      };
    }

    if (!SUPPORTED_VERSIONS.includes(requested)) {
      return {
        code: UNSUPPORTED_VERSION_ERROR,
        message: 'Unsupported protocol version',
        data: { supported: SUPPORTED_VERSIONS, requested },
      };
    }

    return null;
  }

  /** The DiscoverResult returned by `server/discover`. */
  _discoverResult() {
    return this._result({
      supportedVersions: SUPPORTED_VERSIONS,
      capabilities: {
        tools: { listChanged: false },
        resources: {},
        prompts: {},
      },
      ...(this.instructions ? { instructions: this.instructions } : {}),
    }, { cacheable: true });
  }

  /**
   * Wrap a payload in a 2026-07-28 result envelope.
   *
   * Every result carries `resultType`, the server identity in `_meta`, and —
   * for list results — the cache hints that let a client keep the catalogue
   * warm across reconnects.
   */
  _result(payload, { isError = false, cacheable = false } = {}) {
    const result = {
      resultType: 'complete',
      ...payload,
      _meta: {
        [META_KEYS.SERVER_INFO]: { name: this.name, version: this.version },
      },
    };
    if (isError) result.isError = true;
    if (cacheable) {
      result.ttlMs = this.listTtlMs;
      result.cacheScope = this.cacheScope;
    }
    return result;
  }

  /** The wire form of a tool definition. */
  _toolDefinition(tool) {
    const def = {
      name: tool.name,
      description: tool.description,
      inputSchema: tool.inputSchema,
    };
    if (tool.title) def.title = tool.title;
    if (tool.outputSchema) def.outputSchema = tool.outputSchema;
    if (tool.annotations) def.annotations = tool.annotations;
    return def;
  }

  _send(payload) {
    process.stdout.write(`${JSON.stringify(payload)}\n`);
  }
}

/**
 * Normalize a tool handler's return value into MCP content blocks.
 *
 * A handler may return a string, an array of content blocks, a block with a
 * `type`, or a plain object. Plain objects become both a JSON text block and
 * `structuredContent`, so a client can consume either shape.
 */
export function normalizeToolResult(out) {
  if (out === undefined || out === null) {
    return { content: [{ type: 'text', text: 'OK' }] };
  }
  if (typeof out === 'string') {
    return { content: [{ type: 'text', text: out }] };
  }
  if (Array.isArray(out?.content)) {
    return out;
  }
  if (out?.type === 'image' || out?.type === 'audio' || out?.type === 'resource') {
    return { content: [out] };
  }
  // A multi-round-trip request asks the client to supply input before the
  // call can complete. The answers arrive on the retry as inputResponses.
  if (out?.resultType === 'input_required') {
    return {
      resultType: 'input_required',
      content: [{ type: 'text', text: out.content?.[0]?.text ?? 'Input required' }],
      requests: out.requests ?? [],
    };
  }
  const json = JSON.stringify(out, null, 2);
  return {
    content: [{ type: 'text', text: json }],
    structuredContent: out,
  };
}
