// A message-level simulator for the Max objects the devices' control logic is built from, so the tests can run the
// built patchers' actual cords (the MIDI trigger chain, the link switch, the Live API wiring) without Max.
//
// Messages are arrays: [3] an int, [36, 100] a list, ["bang"], ["trigon", 11]. Objects keep Max's rules that matter
// here: only the left inlet triggers output, right inlets store; trigger and unpack fire right to left. Anything not
// modelled (gen~, jsui, live.path, live.observer, buffer~, ...) records what reaches it, and a test can make any box
// output with emit().

"use strict";

function num(atom) {
    return typeof atom === "number" ? atom : Number(atom);
}

function isFloatArg(a) {
    return a !== undefined && String(a).includes(".");
}

class MaxSim {
    constructor(patcher) {
        const top = patcher.patcher || patcher;
        this.boxes = new Map(top.boxes.map((b) => [b.box.id, b.box]));
        this.cords = new Map(); // "id:outlet" -> [[dst, inlet]]
        for (const { patchline: l } of top.lines) {
            const key = `${l.source[0]}:${l.source[1]}`;
            if (!this.cords.has(key)) {
                this.cords.set(key, []);
            }
            this.cords.get(key).push(l.destination);
        }
        this.state = new Map();
        this.received = new Map(); // id -> [[inlet, msg]] for objects that are not modelled
        this.sent = new Map(); // send name -> [msg]
        this.depth = 0;
    }

    find(pred) {
        for (const [id, box] of this.boxes) {
            if (pred(box)) {
                return id;
            }
        }
        throw new Error("maxsim: no such box");
    }

    byText(text) {
        return this.find((b) => b.text === text);
    }

    byVarname(name) {
        return this.find((b) => b.varname === name);
    }

    emit(id, outlet, msg) {
        if (++this.depth > 1000) {
            throw new Error("maxsim: message loop");
        }
        for (const [dst, inlet] of this.cords.get(`${id}:${outlet}`) || []) {
            this.deliver(dst, inlet, msg);
        }
        this.depth--;
    }

    // What a not-modelled object got, as [inlet, msg] pairs.
    got(id) {
        return this.received.get(id) || [];
    }

    // A [receive name] outputs msg.
    receive(name, msg) {
        for (const [id, box] of this.boxes) {
            if (box.text === `r ${name}` || box.text === `receive ${name}`) {
                this.emit(id, 0, msg);
            }
        }
    }

    st(id, init) {
        if (!this.state.has(id)) {
            this.state.set(id, init());
        }
        return this.state.get(id);
    }

    deliver(id, inlet, msg) {
        const box = this.boxes.get(id);
        const text = box.maxclass === "newobj" ? box.text : null;
        const [cls, ...args] = (text || box.maxclass).split(" ");
        const out = (outlet, m) => this.emit(id, outlet, m);
        switch (cls) {
            case "t":
            case "trigger":
                for (let i = args.length - 1; i >= 0; i--) {
                    if (args[i] === "b") {
                        out(i, ["bang"]);
                    } else if (args[i] === "i") {
                        out(i, [Math.trunc(num(msg[0]))]);
                    } else if (args[i] === "f") {
                        out(i, [num(msg[0])]);
                    } else {
                        out(i, msg);
                    }
                }
                return;
            case "unpack":
                for (let i = Math.min(args.length, msg.length) - 1; i >= 0; i--) {
                    out(i, [msg[i]]);
                }
                return;
            case "+":
            case "-":
            case "*":
            case "==":
            case ">": {
                const s = this.st(id, () => ({ right: num(args[0] || 0) }));
                if (inlet === 1) {
                    s.right = isFloatArg(args[0]) ? num(msg[0]) : Math.trunc(num(msg[0]));
                    return;
                }
                if (msg[0] === "bang") {
                    return;
                }
                const a = isFloatArg(args[0]) ? num(msg[0]) : Math.trunc(num(msg[0]));
                const r = {
                    "+": a + s.right,
                    "-": a - s.right,
                    "*": a * s.right,
                    "==": a === s.right ? 1 : 0,
                    ">": a > s.right ? 1 : 0,
                }[cls];
                out(0, [r]);
                return;
            }
            case "gate": {
                const s = this.st(id, () => ({ open: Math.trunc(num(args[1] || 0)) }));
                if (inlet === 0) {
                    s.open = Math.trunc(num(msg[0]));
                } else if (s.open > 0 && s.open <= num(args[0])) {
                    out(s.open - 1, msg);
                }
                return;
            }
            case "switch": {
                const s = this.st(id, () => ({ sel: Math.trunc(num(args[1] || 0)) }));
                if (inlet === 0) {
                    s.sel = Math.trunc(num(msg[0]));
                } else if (s.sel === inlet) {
                    out(0, msg);
                }
                return;
            }
            case "split": {
                const s = this.st(id, () => ({ lo: num(args[0]), hi: num(args[1]) }));
                if (inlet === 1) {
                    s.lo = num(msg[0]);
                } else if (inlet === 2) {
                    s.hi = num(msg[0]);
                } else {
                    const v = num(msg[0]);
                    out(v >= s.lo && v <= s.hi ? 0 : 1, [v]);
                }
                return;
            }
            case "counter": {
                const lo = args.length >= 2 ? num(args[0]) : 0;
                const hi = args.length >= 2 ? num(args[1]) : num(args[0]);
                const s = this.st(id, () => ({ c: lo }));
                if (inlet === 0) {
                    out(0, [s.c]);
                    s.c = s.c + 1 > hi ? lo : s.c + 1;
                }
                return;
            }
            case "prepend":
                out(0, [args.join(" ")].concat(msg[0] === "symbol" ? msg.slice(1) : msg));
                return;
            case "s":
            case "send": {
                if (!this.sent.has(args[0])) {
                    this.sent.set(args[0], []);
                }
                this.sent.get(args[0]).push(msg);
                return;
            }
            case "message":
                if (inlet === 0) {
                    out(0, box.text.split(" ").map((a) => (isNaN(Number(a)) ? a : Number(a))));
                }
                return;
            default:
                if (!this.received.has(id)) {
                    this.received.set(id, []);
                }
                this.received.get(id).push([inlet, msg]);
        }
    }
}

module.exports = { MaxSim };
