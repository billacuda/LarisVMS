// AudioWorklet for push-to-talk (talk.js): turns the microphone's float samples, at whatever rate the
// AudioContext runs (usually 48 kHz), into 16-bit PCM at 8 kHz — what the camera's G.711 backchannel
// takes — and posts it to the main thread in 40 ms chunks. Each output sample is the average of the
// input samples it covers, a simple low-pass that keeps speech clear without aliasing hiss.
class TalkDownsampler extends AudioWorkletProcessor {
    constructor() {
        super();
        this.ratio = sampleRate / 8000;
        this.acc = 0;
        this.accCount = 0;
        this.pos = 0;           // input samples consumed toward the next output sample
        this.chunk = new Int16Array(320); // 40 ms at 8 kHz
        this.chunkLen = 0;
    }

    process(inputs) {
        const input = inputs[0];
        if (!input || input.length === 0) return true;
        const channel = input[0];
        for (let i = 0; i < channel.length; i++) {
            this.acc += channel[i];
            this.accCount++;
            this.pos++;
            if (this.pos >= this.ratio) {
                this.pos -= this.ratio;
                const v = Math.max(-1, Math.min(1, this.acc / this.accCount));
                this.chunk[this.chunkLen++] = v < 0 ? v * 0x8000 : v * 0x7FFF;
                this.acc = 0;
                this.accCount = 0;
                if (this.chunkLen === this.chunk.length) {
                    const out = this.chunk;
                    this.port.postMessage(out.buffer, [out.buffer]);
                    this.chunk = new Int16Array(320);
                    this.chunkLen = 0;
                }
            }
        }
        return true;
    }
}

registerProcessor('talk-downsampler', TalkDownsampler);
