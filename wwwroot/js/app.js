window.leaveApp = {
    downloadBase64: function (filename, base64, contentType) {
        const raw = atob(base64);
        const bytes = new Uint8Array(raw.length);
        for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
        const blob = new Blob([bytes], { type: contentType });
        const url = URL.createObjectURL(blob);
        const link = document.createElement('a');
        link.href = url;
        link.download = filename;
        document.body.appendChild(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1500);
    },
    printPaper: function (elementId) {
        const paper = document.getElementById(elementId);
        if (!paper) return;
        const frame = document.createElement('iframe');
        frame.style.position = 'fixed';
        frame.style.right = '0';
        frame.style.bottom = '0';
        frame.style.width = '0';
        frame.style.height = '0';
        frame.style.border = '0';
        document.body.appendChild(frame);
        const doc = frame.contentWindow.document;
        doc.open();
        doc.write('<!DOCTYPE html><html lang="vi"><head><meta charset="utf-8"><title>Đơn xin nghỉ phép</title><style>' +
            '@page { size: A4; margin: 16mm 17mm 14mm 20mm; } ' +
            '* { box-sizing: border-box; } body { margin: 0; color: #111; font: 14pt/1.23 "Times New Roman", Times, serif; }' +
            '.official-paper { width: 100%; min-height: 0; padding: 0; margin: 0; background: #fff; font: 14pt/1.23 "Times New Roman", Times, serif; }' +
            '.paper-heading { display:flex; flex-direction:column; text-align:center; align-items:center; line-height:1.15; } .paper-heading b { font-size:14pt; }' +
            '.national-motto { border-bottom:1px solid #222; padding-bottom:3px; margin-top:2px; } .paper-dateline { text-align:right; font-style:italic; margin:22pt 0 24pt; }' +
            '.official-paper>h2 { text-align:center; margin:0 0 15pt; font:bold 15pt "Times New Roman"; }' +
            '.paper-greeting { display:flex; gap:14pt; margin:0 0 15pt; } .paper-greeting>b { white-space:nowrap; }' +
            'p { margin:6pt 0; text-align:justify; line-height:1.22; } p.paper-indent { text-indent:1cm; } p.paper-time { padding-left:1cm; margin:2pt 0; }' +
            '.paper-underlined { border-bottom:1px dotted #555; overflow-wrap:anywhere; }' +
            '.paper-applicant { width:43%; margin:10pt 0 0 auto; text-align:center; } .paper-applicant b,.paper-applicant strong { display:block; } .paper-sign-space { height:49pt; }' +
            '.paper-approvers { display:grid; grid-template-columns:1fr 1fr; gap:12pt; text-align:center; margin-top:24pt; font-weight:bold; }' +
            '.paper-footnote { font-size:11pt; line-height:1.3; margin-top:70pt; } .paper-footnote>div { padding-left:46pt; }' +
            '</style></head><body>' + paper.outerHTML + '</body></html>');
        doc.close();
        frame.contentWindow.focus();
        setTimeout(() => {
            try { frame.contentWindow.print(); } finally { setTimeout(() => frame.remove(), 1500); }
        }, 250);
    }
};
