-- Packet callback sandbox. Return nil to pass the frame unchanged.
-- Example return value:
-- return { action = "replace", hex = "00 FF", reason = "test replacement" }
function on_frame(ctx)
    return nil
end

-- Scenario entry point. The host records the returned value as the result.
function main(api)
    api:mark("v5 discovery scenario loaded")
    return true
end
