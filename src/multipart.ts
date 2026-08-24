import { IncomingMessage } from 'node:http';

export interface MultipartResult {
  fields: Record<string, string>;
  file: { filename: string; data: Buffer } | null;
}

/** 极简 RFC7578 multipart/form-data 解析(单文件上传场景,整体读入内存) */
export function parseMultipart(contentType: string | undefined, body: Buffer): MultipartResult {
  const result: MultipartResult = { fields: {}, file: null };
  if (!contentType) return result;
  const m = /boundary=(?:"([^"]+)"|([^;]+))/i.exec(contentType);
  const boundary = m?.[1] ?? m?.[2];
  if (!boundary) return result;

  const firstDelim = Buffer.from('--' + boundary);
  // 分隔符 = CRLF + "--boundary"(RFC 2046:CRLF 属于分隔符,不属于 part 内容)
  const delim = Buffer.from('\r\n--' + boundary);
  const crlf = Buffer.from('\r\n');
  const headerSep = Buffer.from('\r\n\r\n');
  const endMarker = Buffer.from('--');

  let pos = body.indexOf(firstDelim);
  if (pos === -1) return result;
  pos += firstDelim.length;
  if (!body.subarray(pos, pos + 2).equals(crlf)) return result;
  pos += 2;

  while (true) {
    const headerEnd = body.indexOf(headerSep, pos);
    if (headerEnd === -1) break;
    const headerText = body.subarray(pos, headerEnd).toString('utf8');
    pos = headerEnd + 4;

    const next = body.indexOf(delim, pos);
    if (next === -1) break;
    const partBody = body.subarray(pos, next);
    pos = next + delim.length;

    // 解析 Content-Disposition (支持 name="...", name=..., filename="...", filename=...)
    const nameMatch = /name=(?:"([^"]*)"|([^;\r\n]+))/i.exec(headerText);
    const fileMatch = /filename=(?:"([^"]*)"|([^;\r\n]+))/i.exec(headerText);
    if (nameMatch) {
      const name = (nameMatch[1] ?? nameMatch[2]).trim();
      if (fileMatch) {
        const filename = (fileMatch[1] ?? fileMatch[2]).trim();
        result.file = { filename, data: Buffer.from(partBody) };
      } else {
        result.fields[name] = partBody.toString('utf8');
      }
    }

    // boundary 之后:'--' 结束;CRLF 进入下一 part;否则畸形退出
    if (body.subarray(pos, pos + 2).equals(endMarker)) break;
    if (!body.subarray(pos, pos + 2).equals(crlf)) break;
    pos += 2;
  }
  return result;
}
